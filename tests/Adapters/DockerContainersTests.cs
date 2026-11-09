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
    private readonly string _workRoot = Path.Combine(TestFolder.Create(), "work");

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
        Assert.Equal("notReady", answer.Type);
        Assert.DoesNotContain("CreateContainerAsync", daemon.Calls);
    }

    [Fact]
    public async Task RunAsync_CodeEndsByItself_ReturnsItsExitCodeOutputAndFiles()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output((1, "rows: 12\n"), (2, "warning: dtype\n"))))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 3 }))
            .Collects(Out("chart.png"));

        var answer = await Containers(daemon).RunAsync("print(1)", [], TimeSpan.FromSeconds(5), CancellationToken.None);

        var outcome = answer.Value!;
        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal("rows: 12\n", outcome.Stdout);
        Assert.Equal("warning: dtype\n", outcome.Stderr);
        Assert.Equal("image/png", Assert.Single(outcome.Outputs).MediaType);
    }

    /// <summary>
    /// Measured on runsc release-20260921.0: past its memory limit the whole gVisor sandbox is killed, the exec answers 128
    /// with the runtime's complaint as stderr, and the container is left exited with OOMKilled set.
    /// </summary>
    [Fact]
    public async Task RunAsync_SandboxKilledWholeForMemory_AnswersTheKillWithoutTheRuntimesWords()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output((2, "waiting on pid 2: urpc method \"containerManager.WaitPID\" failed\n"))))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 128 }))
            .Answer("InspectContainerAsync", _ => Task.FromResult(new ContainerInspectResponse { State = new State { Running = false, OOMKilled = true } }))
            .Collects(Out());

        var outcome = (await Containers(daemon).RunAsync("block = bytearray(400 << 20)", [], TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Equal(DockerContainers.Killed, outcome.ExitCode);
        Assert.Equal(string.Empty, outcome.Stderr);
    }

    /// <summary>Measured the moment the exec ended: the daemon answered «removing» with OOMKilled not yet set.</summary>
    [Fact]
    public async Task RunAsync_ContainerBeingRemovedAfterTheRun_CountsAsKilled()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output()))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 128 }))
            .Answer("InspectContainerAsync", _ => Task.FromResult(new ContainerInspectResponse { State = new State { Status = "removing", Running = false, OOMKilled = false } }))
            .Collects(Out());

        var outcome = (await Containers(daemon).RunAsync("block = bytearray(400 << 20)", [], TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Equal(DockerContainers.Killed, outcome.ExitCode);
    }

    /// <summary>Measured in a full live suite: the daemon still reported the killed sandbox running the moment its exec ended.</summary>
    [Fact]
    public async Task RunAsync_SandboxStillReportedRunningAMoment_CountsAsKilledOnceItStops()
    {
        var asked = 0;
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output()))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = DockerContainers.Lost }))
            .Answer("InspectContainerAsync", _ => ++asked < 3
                ? Task.FromResult(new ContainerInspectResponse { State = new State { Running = true } })
                : Task.FromException<ContainerInspectResponse>(new DockerContainerNotFoundException(HttpStatusCode.NotFound, "No such container")))
            .CollectsFromAGoneContainer();

        var outcome = (await Containers(daemon).RunAsync("block = bytearray(400 << 20)", [], TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Equal(DockerContainers.Killed, outcome.ExitCode);
    }

    /// <summary>A program may exit 128 itself; its container then goes on idling, and the code stays the program's.</summary>
    [Fact]
    public async Task RunAsync_ProgramExits128ItselfInALiveContainer_KeepsItsCode()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output((2, "fatal: 128\n"))))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = DockerContainers.Lost }))
            .Collects(Out());

        var outcome = (await Containers(daemon).RunAsync("raise SystemExit(128)", [], TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Equal(DockerContainers.Lost, outcome.ExitCode);
        Assert.Equal("fatal: 128\n", outcome.Stderr);
    }

    /// <summary>An ordinary failure is the program's: nothing is asked of the container, so it costs no wait.</summary>
    [Fact]
    public async Task RunAsync_ProgramFailsOrdinarily_AsksNothingOfTheContainer()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output((2, "KeyError: 'total'\n"))))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 1 }))
            .Collects(Out());

        await Containers(daemon).RunAsync("rows['total']", [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.DoesNotContain("InspectContainerAsync", daemon.Calls);
    }

    /// <summary>A killed gVisor container removes itself, and can be gone before the server asks about it.</summary>
    [Fact]
    public async Task RunAsync_ContainerGoneAfterTheRun_CountsAsKilledAndSaysWhyNoFileCameBack()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output()))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 128 }))
            .Answer("InspectContainerAsync", _ => Task.FromException<ContainerInspectResponse>(new DockerContainerNotFoundException(HttpStatusCode.NotFound, "No such container")))
            .CollectsFromAGoneContainer();

        var outcome = (await Containers(daemon).RunAsync("block = bytearray(400 << 20)", [], TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Equal(DockerContainers.Killed, outcome.ExitCode);
        Assert.Contains("killed", Assert.Single(outcome.Omitted), StringComparison.Ordinal);
    }

    /// <summary>GNU tar answers 1 when a file changed while it read it; the archive is whole and the files come back.</summary>
    [Fact]
    public async Task RunAsync_FileChangedWhileTarReadIt_StillReturnsTheFiles()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output()))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 0 }))
            .Collects(Out("table.csv"), exitCode: 1);

        var outcome = (await Containers(daemon).RunAsync("print(1)", [], TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Equal("table.csv", Assert.Single(outcome.Outputs).Name);
    }

    /// <summary>What tar says on its standard error is no part of the archive, which comes back whole.</summary>
    [Fact]
    public async Task RunAsync_TarWarnsWhileReading_StillReturnsTheFiles()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output()))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 0 }))
            .Collects(Out("table.csv"), warning: "tar: out/table.csv: file changed as we read it\n");

        var outcome = (await Containers(daemon).RunAsync("print(1)", [], TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Equal("table.csv", Assert.Single(outcome.Outputs).Name);
    }

    /// <summary>tar failing outright is said, not passed off as a run that wrote nothing.</summary>
    [Fact]
    public async Task RunAsync_TarFailsToReadOut_SaysWhyNoFileCameBack()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output()))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 0 }))
            .Collects(Out("table.csv"), exitCode: 2);

        var outcome = (await Containers(daemon).RunAsync("print(1)", [], TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Empty(outcome.Outputs);
        Assert.Contains("exit 2", Assert.Single(outcome.Omitted), StringComparison.Ordinal);
    }

    /// <summary>The archive is held in memory before reading, so what one run can make the server hold stops at twice /out.</summary>
    [Fact]
    public async Task RunAsync_ArchivePastTwiceTheOutFolder_ReturnsNoFileAndSaysSo()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output()))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 0 }))
            .Collects(new MemoryStream(new byte[4096]));
        var containers = new DockerContainers(daemon.Client, Options.Create(new SandboxOptions { WorkRoot = _workRoot, OutFolderBytes = 1024 }), NullLogger<DockerContainers>.Instance);

        var outcome = (await containers.RunAsync("print(1)", [], TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;

        Assert.Empty(outcome.Outputs);
        Assert.Contains("2048", Assert.Single(outcome.Omitted), StringComparison.Ordinal);
    }

    /// <summary>A container that stopped with its run refuses a new exec with 409; that is the run killed, not the sandbox failing.</summary>
    [Fact]
    public async Task RunAsync_ContainerStoppedBeforeTheCollection_SaysWhyNoFileCameBack()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(Output()))
            .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 0 }))
            .Answer("CreateContainerExecAsync", arguments => arguments[1] is ContainerExecCreateParameters { Cmd: ["tar", ..] }, Collecting.Collect,
                _ => Task.FromException<ContainerExecCreateResponse>(new DockerApiException(HttpStatusCode.Conflict, "container is not running")));

        var answer = await Containers(daemon).RunAsync("print(1)", [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(answer.IsSuccess);
        Assert.Contains("killed", Assert.Single(answer.Value!.Omitted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CodeOutlivesTheTimeout_IsReportedAsTimedOutAndItsContainerRemoved()
    {
        var daemon = Ready()
            .Answer("StartContainerExecAsync", _ => Task.FromResult(new MultiplexedStream(new Pipe().Reader.AsStream(), multiplexed: true)));

        var answer = await Containers(daemon).RunAsync("while True: pass", [], TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.True(answer.Value!.HasTimedOut);
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

        Assert.True(answer.Value!.HasTimedOut);
        Assert.DoesNotContain(Collecting.Collect, daemon.Calls);
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
        Assert.Equal("notReady", answer.Type);
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

        var readiness = answer.Value!;
        Assert.Equal("29.1.0", readiness.DockerVersion);
        Assert.Equal("runsc", readiness.Runtime);
        Assert.Equal("snail/sandbox-python:latest", readiness.Image);
        Assert.Empty(readiness.Untestable);
    }

    /// <summary>The server never pulls: a node without a toolchain's image still runs code, and says which projects it cannot test.</summary>
    [Fact]
    public async Task DiagnoseAsync_NodeWithoutTheDotnetImage_NamesDotnetUntestableAndStaysReady()
    {
        var daemon = new Daemon()
            .Answer("GetSystemInfoAsync", _ => Task.FromResult(System("runc", "runsc")))
            .Answer("InspectImageAsync", arguments => arguments[0] as string == new SandboxOptions().DotnetImage
                ? Task.FromException<ImageInspectResponse>(new DockerImageNotFoundException(HttpStatusCode.NotFound, "No such image"))
                : Task.FromResult(new ImageInspectResponse()));

        var answer = await Containers(daemon).DiagnoseAsync(CancellationToken.None);

        Assert.True(answer.IsSuccess);
        Assert.Equal([Toolchain.Dotnet], answer.Value!.Untestable);
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
        Assert.Equal("notReady", answer.Type);
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
        .Answer("InspectContainerAsync", _ => Task.FromResult(new ContainerInspectResponse { State = new State { Running = true } }))
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
