using System.Formats.Tar;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Adapters;

public sealed class DockerContainersTests : IDisposable
{
    private readonly string _workRoot = Path.Combine(Path.GetTempPath(), $"sandbox-tests-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (Directory.Exists(_workRoot))
            Directory.Delete(_workRoot, recursive: true);
    }

    /// <summary>Running under the default runtime instead would work, look the same and drop gVisor.</summary>
    [Fact]
    public async Task RunAsync_RuntimeUnknownToTheDaemon_IsRefusedBeforeAnyContainerExists()
    {
        var daemon = new Daemon().Answer("GetSystemInfoAsync", _ => Task.FromResult(System("runc")));

        var answer = await Containers(daemon).RunAsync("print(1)", [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("runsc", answer.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateContainerAsync", daemon.Calls);
    }

    [Fact]
    public async Task RunAsync_CodeEndsByItself_ReturnsItsExitCodeOutputAndFiles()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output((1, "rows: 12\n"), (2, "warning: dtype\n"))))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 3 }))
            .Answer("GetArchiveFromContainerAsync", _ => Task.FromResult(new ContainerArchiveResponse { Stream = Out("chart.png") }));

        var answer = await Containers(daemon).RunAsync("print(1)", [], TimeSpan.FromSeconds(5), CancellationToken.None);

        var outcome = answer.Value!;
        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal("rows: 12\n", outcome.Stdout);
        Assert.Equal("warning: dtype\n", outcome.Stderr);
        Assert.Equal("image/png", Assert.Single(outcome.Outputs).MediaType);
    }

    [Fact]
    public async Task RunAsync_CodeOutlivesTheTimeout_IsReportedAsTimedOutAndItsContainerRemoved()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(new MultiplexedStream(new Pipe().Reader.AsStream(), multiplexed: true)));

        var answer = await Containers(daemon).RunAsync("while True: pass", [], TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.True(answer.Value!.TimedOut);
        Assert.Null(answer.Value.ExitCode);
        Assert.Equal("RemoveContainerAsync", daemon.Calls[^1]);
    }

    /// <summary>Output ending is not the process ending: code can close its streams and go on running.</summary>
    [Fact]
    public async Task RunAsync_OutputEndsButTheProcessRunsOn_CountsAsTimedOutAndCollectsNothing()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output()))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = true }));

        var answer = await Containers(daemon).RunAsync("import os; os.close(1)", [], TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.True(answer.Value!.TimedOut);
        Assert.DoesNotContain("GetArchiveFromContainerAsync", daemon.Calls);
    }

    [Fact]
    public async Task RunAsync_CopyIntoTheContainerFails_StillRemovesTheContainer()
    {
        var daemon = Ready()
            .Answer("ExtractArchiveToContainerAsync", _ => Task.FromException(new DockerApiException(HttpStatusCode.InternalServerError, "no space left on device")));

        var answer = await Containers(daemon).RunAsync("print(1)", [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Equal("RemoveContainerAsync", daemon.Calls[^1]);
    }

    [Fact]
    public async Task RunAsync_ImageMissing_AnswersThatTheNodeIsNotSetUp()
    {
        var daemon = new Daemon()
            .Answer("GetSystemInfoAsync", _ => Task.FromResult(System("runc", "runsc")))
            .Answer("CreateContainerAsync", _ => Task.FromException<CreateContainerResponse>(new DockerImageNotFoundException(HttpStatusCode.NotFound, "No such image")));

        var answer = await Containers(daemon).RunAsync("print(1)", [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("snail/sandbox-python:latest", answer.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnoseAsync_NodeWithRuntimeAndImage_SaysWhatItRunsWith()
    {
        var daemon = new Daemon()
            .Answer("GetSystemInfoAsync", _ =>
            {
                var system = System("runc", "runsc");
                system.ServerVersion = "29.1.0";
                return Task.FromResult(system);
            })
            .Answer("InspectImageAsync", _ => Task.FromResult(new ImageInspectResponse()));

        var answer = await Containers(daemon).DiagnoseAsync(CancellationToken.None);

        Assert.Equal(new Readiness("29.1.0", "runsc", "snail/sandbox-python:latest"), answer.Value);
    }

    /// <summary>The probe that passes on a node without gVisor is the one this tool exists to fail.</summary>
    [Fact]
    public async Task DiagnoseAsync_NodeWithoutTheRuntime_FailsNamingItAndStartsNothing()
    {
        var daemon = new Daemon().Answer("GetSystemInfoAsync", _ => Task.FromResult(System("runc")));

        var answer = await Containers(daemon).DiagnoseAsync(CancellationToken.None);

        Assert.Contains("runsc", answer.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateContainerAsync", daemon.Calls);
    }

    [Fact]
    public async Task DiagnoseAsync_NodeWithoutTheImage_FailsNamingIt()
    {
        var daemon = new Daemon()
            .Answer("GetSystemInfoAsync", _ => Task.FromResult(System("runsc")))
            .Answer("InspectImageAsync", _ => Task.FromException<ImageInspectResponse>(new DockerImageNotFoundException(HttpStatusCode.NotFound, "No such image")));

        var answer = await Containers(daemon).DiagnoseAsync(CancellationToken.None);

        Assert.Contains("snail/sandbox-python:latest", answer.Error, StringComparison.Ordinal);
    }

    private DockerContainers Containers(Daemon daemon) =>
        new(daemon.Client, Options.Create(new SandboxOptions { WorkRoot = _workRoot }), NullLogger<DockerContainers>.Instance);

    /// <summary>A daemon with runsc that creates, fills, starts and removes a container without complaint.</summary>
    private static Daemon Ready() => new Daemon()
        .Answer("GetSystemInfoAsync", _ => Task.FromResult(System("runc", "runsc")))
        .Answer("CreateContainerAsync", _ => Task.FromResult(new CreateContainerResponse { ID = "run-1", Warnings = [] }))
        .Answer("ExtractArchiveToContainerAsync", _ => Task.CompletedTask)
        .Answer("StartContainerAsync", _ => Task.FromResult(true))
        .Answer("CreateContainerExecAsync", _ => Task.FromResult(new ContainerExecCreateResponse { ID = "exec-1" }))
        .Answer("RemoveContainerAsync", _ => Task.CompletedTask);

    private static SystemInfoResponse System(params string[] runtimes) => new()
    {
        Runtimes = runtimes.ToDictionary(runtime => runtime, _ => new RuntimeWithStatus())
    };

    /// <summary>An exec's attached output as the daemon frames it: a stream byte, three zeros, a big-endian length.</summary>
    private static MultiplexedStream Output(params (byte Stream, string Text)[] frames)
    {
        var framed = new MemoryStream();

        foreach (var (stream, text) in frames)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            framed.Write([stream, 0, 0, 0, .. BitConverter.GetBytes(payload.Length).Reverse()]);
            framed.Write(payload);
        }

        framed.Position = 0;

        return new MultiplexedStream(framed, multiplexed: true);
    }

    private static MemoryStream Out(params string[] names)
    {
        var archive = new MemoryStream();

        using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "out/"));
            foreach (var name in names)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"out/{name}") { DataStream = new MemoryStream([1, 2, 3]) });
        }

        archive.Position = 0;

        return archive;
    }
}
