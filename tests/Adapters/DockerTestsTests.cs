using System.Formats.Tar;
using System.IO.Pipelines;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Adapters;

/// <summary>
/// A project restored, built and tested as stages of one container: a failed stage stops the run, the restore network
/// is gone before the build, the package volume is handed over once, and the container is removed whatever happened.
/// </summary>
public sealed class DockerTestsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("sandbox-docker-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task TestAsync_EveryStagePasses_ReadsTheCountsFromTheReports()
    {
        var daemon = Ready(exitCodes: [0, 0, 0])
            .Collects(Reports());

        var execution = (await Tests(daemon).TestAsync(await ProjectAsync(), Toolchain.Dotnet, TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Equal([Stage.Restore, Stage.Build, Stage.Test], execution.Stages.Select(stage => stage.Stage));
        Assert.Equal(2, execution.Counts!.Total);
        Assert.Equal(1, execution.Counts.Failed);
    }

    /// <summary>A build after a failed restore would only repeat the restore's error in worse words.</summary>
    [Fact]
    public async Task TestAsync_RestoreFails_StopsThereAndReadsNoReport()
    {
        var daemon = Ready(exitCodes: [1]);

        var execution = (await Tests(daemon).TestAsync(await ProjectAsync(), Toolchain.Dotnet, TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Equal(Stage.Restore, Assert.Single(execution.Stages).Stage);
        Assert.Null(execution.Counts);
        Assert.DoesNotContain(Collecting.Collect, daemon.Calls);
        Assert.Equal("RemoveContainerAsync", daemon.Calls[^1]);
    }

    [Fact]
    public async Task TestAsync_RestoreNetworkNamed_IsDisconnectedAfterTheRestoreAndBeforeTheBuild()
    {
        var daemon = Ready(exitCodes: [0, 1])
            .Answer("DisconnectNetworkAsync", _ => Task.CompletedTask);

        await Tests(daemon, new SandboxOptions { WorkRoot = _folder, RestoreNetwork = "packages" }).TestAsync(await ProjectAsync(), Toolchain.Dotnet, TimeSpan.FromSeconds(5), CancellationToken.None);

        var execs = daemon.Calls.Select((call, index) => call == "StartContainerExecAsync" ? index : -1).Where(index => index >= 0).ToList();
        var disconnected = daemon.Calls.IndexOf("DisconnectNetworkAsync");
        Assert.InRange(disconnected, execs[0], execs[1]);
    }

    /// <summary>A second disconnect of a network the container has already left is an error from the daemon, and the run would fail on it.</summary>
    [Fact]
    public async Task TestAsync_ThreeStagesPass_DisconnectsTheRestoreNetworkOnce()
    {
        var daemon = Ready(exitCodes: [0, 0, 0])
            .Answer("DisconnectNetworkAsync", _ => Task.CompletedTask)
            .Collects(Reports());

        await Tests(daemon, new SandboxOptions { WorkRoot = _folder, RestoreNetwork = "packages" }).TestAsync(await ProjectAsync(), Toolchain.Dotnet, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Single(daemon.Calls, call => call == "DisconnectNetworkAsync");
        Assert.Single(daemon.Calls, call => call == "InspectContainerAsync");
    }

    [Fact]
    public async Task TestAsync_NoRestoreNetwork_DisconnectsNothingButStillChecks()
    {
        var daemon = Ready(exitCodes: [0, 1]);

        await Tests(daemon).TestAsync(await ProjectAsync(), Toolchain.Dotnet, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.DoesNotContain("DisconnectNetworkAsync", daemon.Calls);
        Assert.Contains("InspectContainerAsync", daemon.Calls);
    }

    /// <summary>The build runs project code; a container still on a network after the restore gets no build at all.</summary>
    [Fact]
    public async Task TestAsync_StillAttachedToANetworkAfterTheRestore_BuildsNothingAndRemovesTheContainer()
    {
        var daemon = Ready(exitCodes: [0, 0, 0])
            .Answer("DisconnectNetworkAsync", _ => Task.CompletedTask)
            .Answer("InspectContainerAsync", _ => Task.FromResult(Attached("packages")));

        var answer = await Tests(daemon, new SandboxOptions { WorkRoot = _folder, RestoreNetwork = "packages" }).TestAsync(await ProjectAsync(), Toolchain.Dotnet, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("packages", answer.Error, StringComparison.Ordinal);
        Assert.Single(daemon.Calls, call => call == "StartContainerExecAsync");
        Assert.Equal("RemoveContainerAsync", daemon.Calls[^1]);
    }

    [Fact]
    public async Task TestAsync_TwoRunsOfOneToolchain_HandTheVolumeOverOnce()
    {
        var daemon = Ready(exitCodes: [1, 1]);
        var tests = Tests(daemon);

        await tests.TestAsync(await ProjectAsync(), Toolchain.Python, TimeSpan.FromSeconds(5), CancellationToken.None);
        await tests.TestAsync(await ProjectAsync(), Toolchain.Python, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Single(daemon.Calls, call => call == "WaitContainerAsync");
    }

    [Fact]
    public async Task TestAsync_HandoverFails_RunsNoStage()
    {
        var daemon = Ready(exitCodes: [0])
            .Answer("WaitContainerAsync", _ => Task.FromResult(new ContainerWaitResponse { StatusCode = 1 }));

        var answer = await Tests(daemon).TestAsync(await ProjectAsync(), Toolchain.Node, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("chown", answer.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateContainerExecAsync", daemon.Calls);
    }

    [Fact]
    public async Task TestAsync_RuntimeUnknownToTheDaemon_IsRefusedBeforeAnyContainerExists()
    {
        var daemon = new Daemon().Answer("GetSystemInfoAsync", _ => Task.FromResult(System("runc")));

        var answer = await Tests(daemon).TestAsync(await ProjectAsync(), Toolchain.Dotnet, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Contains("runsc", answer.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateContainerAsync", daemon.Calls);
    }

    [Fact]
    public async Task TestAsync_RestoreOutlivesTheWholeTimeout_EndsTimedOutWithoutABuild()
    {
        var daemon = Ready(exitCodes: [0])
            .Answer("StartContainerExecAsync", _ => Task.FromResult(new MultiplexedStream(new Pipe().Reader.AsStream(), multiplexed: true)));

        var execution = (await Tests(daemon).TestAsync(await ProjectAsync(), Toolchain.Dotnet, TimeSpan.FromMilliseconds(200), CancellationToken.None)).Value!;

        Assert.Null(Assert.Single(execution.Stages).ExitCode);
        Assert.Equal("RemoveContainerAsync", daemon.Calls[^1]);
    }

    [Fact]
    public async Task TestAsync_StageOutput_KeepsStandardErrorAfterStandardOutput()
    {
        var daemon = Ready(exitCodes: [1])
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Framed("Restoring\n", "error NU1101: Unable to find package Nope")));

        var execution = (await Tests(daemon).TestAsync(await ProjectAsync(), Toolchain.Dotnet, TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Equal("Restoring\n\nerror NU1101: Unable to find package Nope", execution.Stages[0].Output);
    }

    /// <summary>Only the restore is given the proxy: the build and the tests run project code, and never have a network to ask for.</summary>
    [Fact]
    public async Task TestAsync_RestoreProxyOn_GivesItToTheRestoreOnlyAndAnswersTheHosts()
    {
        List<IList<string>> environments = [];
        var daemon = Ready(exitCodes: [0, 0, 0])
            .Answer("CreateContainerExecAsync", call => { environments.Add(((ContainerExecCreateParameters)call[1]!).Env); return Task.FromResult(new ContainerExecCreateResponse { ID = "exec-1" }); })
            .Collects(Reports());
        var options = new SandboxOptions { WorkRoot = _folder, RestoreProxy = "http://mcp-sandbox:3128", RestoreProxyPort = 3128 };
        var proxy = new RestoreProxy(Options.Create(options), NullLogger<RestoreProxy>.Instance);

        var execution = (await new DockerTests(daemon.Client, Options.Create(options), NullLogger<DockerTests>.Instance, proxy).TestAsync(await ProjectAsync(), Toolchain.Dotnet, TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Contains(environments[0], variable => variable.StartsWith("HTTPS_PROXY=http://", StringComparison.Ordinal) && variable.EndsWith("@mcp-sandbox:3128", StringComparison.Ordinal));
        Assert.All(environments.Skip(1), environment => Assert.DoesNotContain(environment, variable => variable.Contains("PROXY", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(execution.Contacted);
        Assert.Empty(execution.Contacted);
    }

    /// <summary>Nobody watched a run without the proxy: its hosts are unknown, which is not the same as none.</summary>
    [Fact]
    public async Task TestAsync_NoRestoreProxy_AnswersTheHostsAsUnknown()
    {
        var daemon = Ready(exitCodes: [1]);

        var execution = (await Tests(daemon).TestAsync(await ProjectAsync(), Toolchain.Dotnet, TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Null(execution.Contacted);
    }

    private DockerTests Tests(Daemon daemon, SandboxOptions? options = null) =>
        new(daemon.Client, Options.Create(options ?? new SandboxOptions { WorkRoot = _folder }), NullLogger<DockerTests>.Instance);

    private async Task<PackedProject> ProjectAsync()
    {
        var archive = Path.Combine(_folder, $"{Guid.NewGuid():N}.tar");
        await File.WriteAllBytesAsync(archive, new byte[1024]);

        return new PackedProject(archive, ["Shop.slnx"], []);
    }

    /// <summary>A daemon with runsc on which every exec ends with the next of <paramref name="exitCodes"/>.</summary>
    private static Daemon Ready(int[] exitCodes)
    {
        var next = 0;

        return new Daemon()
            .Answer("GetSystemInfoAsync", _ => Task.FromResult(System("runc", "runsc")))
            .Answer("CreateContainerAsync", _ => Task.FromResult(new CreateContainerResponse { ID = "test-1", Warnings = [] }))
            .Answer("StartContainerAsync", _ => Task.FromResult(true))
            .Answer("WaitContainerAsync", _ => Task.FromResult(new ContainerWaitResponse { StatusCode = 0 }))
            .Answer("ExtractArchiveToContainerAsync", _ => Task.CompletedTask)
            .Answer("InspectContainerAsync", _ => Task.FromResult(Attached()))
            .Answer("CreateContainerExecAsync", _ => Task.FromResult(new ContainerExecCreateResponse { ID = "exec-1" }))
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Framed("ok\n", string.Empty)))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = exitCodes[Math.Min(next++, exitCodes.Length - 1)] }))
            .Answer("RemoveContainerAsync", _ => Task.CompletedTask);
    }

    private static ContainerInspectResponse Attached(params string[] networks) => new()
    {
        NetworkSettings = new NetworkSettings { Networks = networks.ToDictionary(network => network, _ => new EndpointSettings()) }
    };

    private static SystemInfoResponse System(params string[] runtimes) => new()
    {
        Runtimes = runtimes.ToDictionary(runtime => runtime, _ => new RuntimeWithStatus())
    };

    /// <summary>An exec's output as the daemon frames it: standard output, then standard error when there is any.</summary>
    private static MultiplexedStream Framed(string stdout, string stderr)
    {
        var framed = new MemoryStream();
        Frame(framed, 1, stdout);

        if (stderr.Length > 0)
            Frame(framed, 2, stderr);

        framed.Position = 0;

        return new MultiplexedStream(framed, multiplexed: true);
    }

    private static void Frame(MemoryStream framed, byte stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        framed.Write([stream, 0, 0, 0, .. BitConverter.GetBytes(payload.Length).Reverse()]);
        framed.Write(payload);
    }

    private static MemoryStream Reports()
    {
        var archive = new MemoryStream();
        var trx = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
            <UnitTestResult testName="A" outcome="Passed" />
            <UnitTestResult testName="B" outcome="Failed"><Output><ErrorInfo><Message>no</Message></ErrorInfo></Output></UnitTestResult>
            </Results></TestRun>
            """;

        using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "out/"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "out/results/a.trx") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(trx)) });
        }

        archive.Position = 0;

        return archive;
    }
}
