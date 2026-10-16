using System.Text.Json;
using Docker.DotNet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Live;

/// <summary>What a run can and cannot do, asked of the code itself inside a real container.</summary>
/// <remarks>
/// Each check is a program that tries the forbidden thing and prints what happened as JSON. They prove the
/// confinement under whichever runtime <c>SNAIL_SANDBOX_LIVE_RUNTIME</c> names — under runc everything but the
/// kernel boundary, under runsc on a sandbox node that as well. The image is <c>SNAIL_SANDBOX_LIVE_IMAGE</c>, the
/// default executor when unset; build it first.
/// </remarks>
[Collection(LiveFactAttribute.Daemon)]
public sealed class IsolationTests : IDisposable
{
    private static readonly string Runtime = Environment.GetEnvironmentVariable(LiveFactAttribute.RuntimeVariable) ?? string.Empty;

    private static readonly string Image = Environment.GetEnvironmentVariable("SNAIL_SANDBOX_LIVE_IMAGE") is { Length: > 0 } image
        ? image
        : new SandboxOptions().Image;

    private readonly string _folder = Directory.CreateTempSubdirectory("sandbox-live-").FullName;

    private readonly DockerClient _docker = new DockerClientBuilder().WithEndpoint(new Uri(new SandboxOptions().DockerEndpoint)).Build();

    public void Dispose()
    {
        _docker.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [LiveFact]
    public async Task Network_AnyAddress_IsUnreachable()
    {
        var report = await ReportAsync("""
            import json, socket
            def attempt(action):
                try:
                    action()
                    return "reached"
                except OSError as error:
                    return type(error).__name__
            print(json.dumps({
                "ip": attempt(lambda: socket.create_connection(("1.1.1.1", 53), timeout=2)),
                "dns": attempt(lambda: socket.getaddrinfo("example.com", 443)),
                "gateway": attempt(lambda: socket.create_connection(("172.17.0.1", 2375), timeout=2)),
            }))
            """);

        Assert.All(report.EnumerateObject(), attempt => Assert.NotEqual("reached", attempt.Value.GetString()));
    }

    /// <summary>/var/tmp is writable by anyone on the image's own root, so only the read-only root keeps it closed.</summary>
    [LiveFact]
    public async Task Filesystem_EverythingOutsideOutAndTmp_IsUnwritable()
    {
        var report = await ReportAsync("""
            import json
            def attempt(path):
                try:
                    open(path, "a").write("x")
                    return "written"
                except OSError as error:
                    return type(error).__name__
            print(json.dumps({path: attempt(path) for path in [
                "/escaped", "/var/tmp/escaped", "/etc/passwd", "/usr/local/bin/python3", "/sandbox/files/sales.csv",
                "/sandbox/files/new.csv", "/sandbox/code/main.py", "/sandbox/escaped"]}))
            """, [Input("sales.csv", "month,total\nJan,10\n")]);

        Assert.All(report.EnumerateObject(), attempt => Assert.NotEqual("written", attempt.Value.GetString()));
    }

    [LiveFact]
    public async Task Inputs_PassedFiles_AreReadableInTheWorkingDirectory()
    {
        var report = await ReportAsync("""
            import json, os
            print(json.dumps({"cwd": os.getcwd(), "files": sorted(os.listdir(".")), "first": open("sales.csv").readline().strip()}))
            """, [Input("sales.csv", "month,total\nJan,10\n")]);

        Assert.Equal("/sandbox/files", report.GetProperty("cwd").GetString());
        Assert.Equal(["sales.csv"], report.GetProperty("files").EnumerateArray().Select(file => file.GetString()));
        Assert.Equal("month,total", report.GetProperty("first").GetString());
    }

    [LiveFact]
    public async Task Privileges_Process_HoldsNoCapabilityAndCannotGainOne()
    {
        var report = await ReportAsync("""
            import json, os
            status = dict(line.split(":\t", 1) for line in open("/proc/self/status").read().splitlines() if ":\t" in line)
            print(json.dumps({"uid": os.getuid(), "effective": status["CapEff"].strip(), "bounding": status["CapBnd"].strip(), "noNewPrivileges": status["NoNewPrivs"].strip()}))
            """);

        Assert.NotEqual(0, report.GetProperty("uid").GetInt32());
        Assert.Equal(0UL, Convert.ToUInt64(report.GetProperty("effective").GetString(), 16));
        Assert.Equal(0UL, Convert.ToUInt64(report.GetProperty("bounding").GetString(), 16));
        Assert.Equal("1", report.GetProperty("noNewPrivileges").GetString());
    }

    [LiveFact]
    public async Task Timeout_ProgramRunningPastIt_IsKilledNearTheLimit()
    {
        var outcome = await RunAsync("import time\nprint('started', flush=True)\ntime.sleep(120)", timeout: TimeSpan.FromSeconds(3));

        Assert.True(outcome.HasTimedOut);
        Assert.Contains("started", outcome.Stdout, StringComparison.Ordinal);
        Assert.InRange(outcome.Duration, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }

    [LiveFact]
    public async Task Memory_AllocationPastTheLimit_IsKilled()
    {
        var outcome = await RunAsync("block = bytearray(400 * 1024 * 1024)\nprint('allocated')", configure: limits => limits.MemoryBytes = 128 * 1024 * 1024);

        Assert.Equal(137, outcome.ExitCode);
        Assert.DoesNotContain("allocated", outcome.Stdout, StringComparison.Ordinal);
    }

    /// <summary>A fork bomb stops at the process limit instead of taking the node; the run itself still ends and answers.</summary>
    [LiveFact]
    public async Task Processes_ThreadsPastTheLimit_AreRefused()
    {
        var report = await ReportAsync("""
            import json, threading, time
            started = 0
            try:
                for _ in range(500):
                    threading.Thread(target=time.sleep, args=(5,), daemon=True).start()
                    started += 1
            except RuntimeError:
                pass
            print(json.dumps({"started": started}))
            """, configure: limits => limits.Pids = 64);

        Assert.InRange(report.GetProperty("started").GetInt32(), 1, 63);
    }

    [LiveFact]
    public async Task Out_WritesPastItsSize_StopAndWhatFitsComesBack()
    {
        var outcome = await RunAsync("""
            import json
            open("/out/small.csv", "w").write("a,b\n1,2\n")
            try:
                open("/out/large.bin", "wb").write(b"0" * (4 * 1024 * 1024))
                print(json.dumps({"large": "written"}))
            except OSError as error:
                print(json.dumps({"large": error.errno}))
            """, configure: limits => limits.OutFolderBytes = 1024 * 1024);

        Assert.Equal(28, JsonDocument.Parse(outcome.Stdout).RootElement.GetProperty("large").GetInt32());
        Assert.Contains(outcome.Outputs, file => file.Name == "small.csv" && file.MediaType == "text/csv");
    }

    [LiveFact]
    public async Task Out_ProgramWrittenThere_CannotBeExecuted()
    {
        var report = await ReportAsync("""
            import json, os, subprocess
            open("/out/run.sh", "w").write("#!/bin/sh\necho escaped\n")
            os.chmod("/out/run.sh", 0o755)
            try:
                subprocess.run(["/out/run.sh"], check=True, capture_output=True)
                print(json.dumps({"exec": "ran"}))
            except OSError as error:
                print(json.dumps({"exec": type(error).__name__}))
            """);

        Assert.NotEqual("ran", report.GetProperty("exec").GetString());
    }

    [LiveFact]
    public async Task Tmp_ProgramWrittenThere_CannotBeExecuted()
    {
        var report = await ReportAsync("""
            import json, os, subprocess
            open("/tmp/run.sh", "w").write("#!/bin/sh\necho escaped\n")
            os.chmod("/tmp/run.sh", 0o755)
            try:
                subprocess.run(["/tmp/run.sh"], check=True, capture_output=True)
                print(json.dumps({"exec": "ran"}))
            except OSError as error:
                print(json.dumps({"exec": type(error).__name__}))
            """);

        Assert.NotEqual("ran", report.GetProperty("exec").GetString());
    }

    /// <summary>A link left in /out comes back named, never followed, now that tar reads /out from inside the container.</summary>
    [LiveFact]
    public async Task Out_LinkToAFileOutside_IsNamedAndNotFollowed()
    {
        var outcome = await RunAsync("""
            import os
            open("/out/kept.txt", "w").write("kept")
            os.symlink("/etc/passwd", "/out/passwd")
            """);

        Assert.Equal(["kept.txt"], outcome.Outputs.Select(file => file.Name));
        Assert.Contains(outcome.Omitted, line => line.StartsWith("passwd", StringComparison.Ordinal));
    }

    /// <summary>Every run above removed its container; none is left for the sweep at the next start to find.</summary>
    [LiveFact]
    public async Task Containers_AfterARun_NoneIsLeft()
    {
        await RunAsync("print(1)");

        var leftovers = await _docker.Containers.ListContainersAsync(Confinement.Leftovers);

        Assert.Empty(leftovers);
    }

    private async Task<JsonElement> ReportAsync(string code, IReadOnlyList<InputFile>? inputs = null, Action<SandboxOptions>? configure = null)
    {
        var outcome = await RunAsync(code, inputs, configure: configure);

        Assert.True(outcome.ExitCode == 0, outcome.Stderr);

        return JsonDocument.Parse(outcome.Stdout).RootElement;
    }

    private async Task<RunOutcome> RunAsync(string code, IReadOnlyList<InputFile>? inputs = null, TimeSpan? timeout = null, Action<SandboxOptions>? configure = null)
    {
        var limits = new SandboxOptions { Runtime = Runtime, Image = Image, WorkRoot = _folder };
        configure?.Invoke(limits);

        var containers = new DockerContainers(_docker, Options.Create(limits), NullLogger<DockerContainers>.Instance);
        var answer = await containers.RunAsync(code, inputs ?? [], timeout ?? TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(answer.IsSuccess, $"{answer.Error} {answer.Hint}");

        return answer.Value!;
    }

    private InputFile Input(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);

        return new InputFile(name, path, new FileInfo(path).Length);
    }
}
