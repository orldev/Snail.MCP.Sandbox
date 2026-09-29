using System.Globalization;
using System.Diagnostics;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>Runs code in a container made for one run on the node's own Docker daemon, and removes it afterwards.</summary>
/// <remarks>
/// The runtime is checked on every run rather than once at start: a daemon reconfigured without runsc would
/// otherwise keep receiving runs under a name it no longer maps to gVisor. The container is created, filled,
/// started, used for exactly one exec and removed in a finally, whatever happened in between.
/// </remarks>
public sealed class DockerContainers(IDockerClient docker, IOptions<SandboxOptions> options, ILogger<DockerContainers> logger) : IContainers
{
    private static readonly TimeSpan ExitPoll = TimeSpan.FromMilliseconds(100);

    /// <summary>The exit code of a command the kernel killed, which the answer to the model reads as passing the memory limit.</summary>
    internal const int Killed = 137;

    /// <summary>The exit code runsc answers for an exec whose sandbox died under it, as the program's own code is lost.</summary>
    internal const int Lost = 128;

    /// <summary>How long a sandbox killed whole may still be reported running after its exec has ended.</summary>
    private static readonly TimeSpan KillSettles = TimeSpan.FromSeconds(1);

    /// <summary>How long reading /out back may take; tar over a tmpfs of tens of megabytes ends well within it.</summary>
    private static readonly TimeSpan CollectWait = TimeSpan.FromSeconds(30);

    /// <summary>The highest exit code of tar whose archive is whole: GNU tar answers 1 when a file changed while it read it.</summary>
    private const int ChangedWhileRead = 1;

    public async Task<Answer<RunOutcome>> RunAsync(string code, IReadOnlyList<InputFile> inputs, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var limits = options.Value;

        try
        {
            var system = await docker.System.GetSystemInfoAsync(cancellationToken);
            if (!system.Runtimes.ContainsKey(limits.Runtime))
                return UnknownRuntime<RunOutcome>(logger, limits.Runtime, [.. system.Runtimes.Keys]);

            return Answer.Ok(await ConfinedAsync(code, inputs, timeout, limits, cancellationToken));
        }
        catch (DockerImageNotFoundException)
        {
            return MissingImage<RunOutcome>(logger, limits.Image);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "A run did not complete through the Docker daemon at {Endpoint}", limits.DockerEndpoint);

            return Answer.Failed<RunOutcome>(
                $"The sandbox could not run the code: {exception.Message}",
                "This is the sandbox failing, not the code. Call once more; if it fails again, say so in the answer.");
        }
    }

    public async Task<Answer<Readiness>> DiagnoseAsync(CancellationToken cancellationToken)
    {
        var limits = options.Value;

        try
        {
            var system = await docker.System.GetSystemInfoAsync(cancellationToken);
            if (!system.Runtimes.ContainsKey(limits.Runtime))
                return UnknownRuntime<Readiness>(logger, limits.Runtime, [.. system.Runtimes.Keys]);

            await docker.Images.InspectImageAsync(limits.Image, cancellationToken);

            return Answer.Ok(new Readiness(system.ServerVersion, limits.Runtime, limits.Image) { Untestable = await UntestableAsync(limits, cancellationToken) });
        }
        catch (DockerImageNotFoundException)
        {
            return MissingImage<Readiness>(logger, limits.Image);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The Docker daemon at {Endpoint} did not answer a diagnosis", limits.DockerEndpoint);

            return Answer.Failed<Readiness>(
                $"The Docker daemon at {limits.DockerEndpoint} did not answer: {exception.Message}",
                "The sandbox server cannot reach the node's Docker socket: check that the job mounts it and adds the socket's group.");
        }
    }

    /// <summary>The toolchains whose test image the node does not hold; Python's is the executor's, already found.</summary>
    private async Task<IReadOnlyList<Toolchain>> UntestableAsync(SandboxOptions limits, CancellationToken cancellationToken)
    {
        List<Toolchain> untestable = [];

        foreach (var toolchain in Enum.GetValues<Toolchain>())
        {
            try
            {
                await docker.Images.InspectImageAsync(TestCommands.Image(toolchain, limits), cancellationToken);
            }
            catch (DockerImageNotFoundException)
            {
                logger.LogWarning("The node holds no {Image}; {Toolchain} projects cannot be tested", TestCommands.Image(toolchain, limits), toolchain);
                untestable.Add(toolchain);
            }
        }

        return untestable;
    }

    /// <summary>The refusal when the daemon has no runtime of the configured name.</summary>
    /// <remarks>
    /// A refusal rather than a run under the default runtime: that run would work, look the same, and be
    /// separated from the host kernel by nothing but namespaces — the one layer gVisor exists to add.
    /// </remarks>
    internal static Answer<T> UnknownRuntime<T>(ILogger logger, string runtime, IReadOnlyList<string> known)
    {
        logger.LogError("The Docker daemon has no runtime {Runtime}, only {Known}; every run is refused", runtime, string.Join(", ", known));

        return Answer.Failed<T>(
            $"The sandbox node has no {runtime} runtime, and no code runs there until it has.",
            "Calling again will not help: the sandbox node is not set up. Say so in the answer.");
    }

    internal static Answer<T> MissingImage<T>(ILogger logger, string image)
    {
        logger.LogWarning("Image {Image} is not on this node; every run fails until it is", image);

        return Answer.Failed<T>(
            $"The image {image} is not on the sandbox node, and no code runs there until it is.",
            "Calling again will not help: the sandbox node is not set up. Say so in the answer.");
    }

    private async Task<RunOutcome> ConfinedAsync(string code, IReadOnlyList<InputFile> inputs, TimeSpan timeout, SandboxOptions limits, CancellationToken cancellationToken)
    {
        var container = await docker.Containers.CreateContainerAsync(Confinement.Container(limits, timeout), cancellationToken);

        try
        {
            foreach (var warning in container.Warnings ?? [])
                logger.LogWarning("Docker warned while creating {Container}: {Warning}", container.ID, warning);

            await PlaceAsync(container.ID, code, inputs, limits, cancellationToken);
            await docker.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), cancellationToken);

            return await ExecuteAsync(container.ID, timeout, limits, cancellationToken);
        }
        finally
        {
            await RemoveAsync(container.ID);
        }
    }

    /// <summary>Copies the code and the inputs into the created container, through an archive that deletes itself.</summary>
    private async Task PlaceAsync(string containerId, string code, IReadOnlyList<InputFile> inputs, SandboxOptions limits, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(limits.WorkRoot);

        await using var archive = new FileStream(
            Path.Combine(limits.WorkRoot, $"{containerId}.tar"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        await RunArchive.PackAsync(archive, code, inputs, cancellationToken);
        archive.Position = 0;

        await docker.Containers.ExtractArchiveToContainerAsync(containerId, new CopyToContainerParameters { Path = Confinement.Root }, archive, cancellationToken);
    }

    /// <summary>Runs the code once, stops waiting at the timeout, and collects /out only from a run that ended by itself.</summary>
    private async Task<RunOutcome> ExecuteAsync(string containerId, TimeSpan timeout, SandboxOptions limits, CancellationToken cancellationToken)
    {
        var exec = await ExecAsync(docker, containerId, Confinement.Execution(limits), timeout, limits.OutputLimitBytes, cancellationToken);

        if (exec.ExitCode is null)
        {
            logger.LogInformation("A run in {Container} passed its {Seconds}s and was killed", containerId, timeout.TotalSeconds);

            return new RunOutcome(null, exec.Stdout.Text, exec.Stderr.Text, exec.Duration) { TimedOut = true, IsStdoutCut = exec.Stdout.IsCut, IsStderrCut = exec.Stderr.IsCut };
        }

        var collected = await CollectAsync(docker, containerId, limits, limits.MaxOutputFiles, cancellationToken);

        return new RunOutcome(exec.ExitCode, exec.Stdout.Text, exec.Stderr.Text, exec.Duration)
        {
            IsStdoutCut = exec.Stdout.IsCut,
            IsStderrCut = exec.Stderr.IsCut,
            Outputs = collected.Files,
            Omitted = collected.Omitted
        };
    }

    /// <summary>Runs one command in the container, stops waiting at the timeout, and answers its exit code, null when it did not end in time.</summary>
    internal static async Task<Exec> ExecAsync(
        IDockerClient docker,
        string containerId,
        ContainerExecCreateParameters parameters,
        TimeSpan timeout,
        int outputLimit,
        CancellationToken cancellationToken)
    {
        var execution = await docker.Exec.CreateContainerExecAsync(containerId, parameters, cancellationToken);
        var stdout = new KeptOutput(outputLimit);
        var stderr = new KeptOutput(outputLimit);
        var started = Stopwatch.GetTimestamp();

        using var stream = await docker.Exec.StartContainerExecAsync(execution.ID, new ContainerExecStartParameters(), cancellationToken);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var drained = DrainAsync(stream, stdout, stderr, reading.Token);
        var exitCode = await EndsWithinAsync(drained, timeout, cancellationToken)
            ? await ExitCodeAsync(docker, execution.ID, timeout - Stopwatch.GetElapsedTime(started), cancellationToken)
            : null;

        if (exitCode is null)
        {
            await reading.CancelAsync();
            Observe(drained);

            return new Exec(null, stdout, stderr, Stopwatch.GetElapsedTime(started));
        }

        if (exitCode == Lost && await IsKilledAsync(docker, containerId, cancellationToken))
            return new Exec(Killed, stdout, new KeptOutput(outputLimit), Stopwatch.GetElapsedTime(started));

        return new Exec(exitCode, stdout, stderr, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>Whether the container was killed under the command, taking the command with it.</summary>
    /// <remarks>
    /// Measured on runsc release-20260921.0: past its memory limit a gVisor sandbox is killed whole, the exec answers
    /// 128 with the runtime's own complaint as its stderr, and the container ends — or is already gone, since it removes
    /// itself. Asked the moment the exec ended, the daemon still answered running in a full live suite, «removing» with
    /// OOMKilled false a moment later, so neither one answer nor the flag is what is read: the container idles far past
    /// the command's timeout, and one that stops running within a second of its exec was killed. Under runc only the
    /// exec dies, with 137, and the container keeps running.
    /// </remarks>
    private static async Task<bool> IsKilledAsync(IDockerClient docker, string containerId, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(KillSettles.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            if (!await IsRunningAsync(docker, containerId, cancellationToken))
                return true;

            if (Stopwatch.GetTimestamp() >= deadline)
                return false;

            await Task.Delay(ExitPoll, cancellationToken);
        }
    }

    private static async Task<bool> IsRunningAsync(IDockerClient docker, string containerId, CancellationToken cancellationToken)
    {
        try
        {
            var inspected = await docker.Containers.InspectContainerAsync(containerId, cancellationToken);

            return inspected.State is not { Running: false };
        }
        catch (DockerApiException exception) when (exception.StatusCode is HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task DrainAsync(MultiplexedStream stream, KeptOutput stdout, KeptOutput stderr, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];

        for (var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
             !read.EOF;
             read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken))
        {
            var target = read.Target is MultiplexedStream.TargetStream.StandardError ? stderr : stdout;
            target.Append(buffer.AsSpan(0, read.Count));
        }
    }

    /// <summary>Whether the output ended before the timeout; the caller's own cancellation still throws.</summary>
    private static async Task<bool> EndsWithinAsync(Task drained, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var first = await Task.WhenAny(drained, Task.Delay(timeout, waiting.Token));
        await waiting.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();

        if (first != drained)
            return false;

        await drained;

        return true;
    }

    /// <summary>The exit code once the exec is recorded as finished, or unknown if it is still running when the time left is up.</summary>
    /// <remarks>
    /// Output ending is not the process ending: code can close its streams and keep running, and even an honest
    /// exit reaches the daemon's record a moment after the last byte.
    /// </remarks>
    private static async Task<int?> ExitCodeAsync(IDockerClient docker, string executionId, TimeSpan left, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(left.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            var inspected = await docker.Exec.InspectContainerExecAsync(executionId, cancellationToken);

            if (!inspected.Running && inspected.ExitCode is { } exitCode)
                return (int)exitCode;

            if (Stopwatch.GetTimestamp() >= deadline)
                return null;

            await Task.Delay(ExitPoll, cancellationToken);
        }
    }

    /// <summary>The files of /out, archived by tar inside the container and copied whole into memory first.</summary>
    /// <remarks>
    /// Measured with Docker.DotNet.Enhanced 4.3.3 over the Unix socket: a TarReader on a response stream itself failed
    /// with EndOfStreamException after the first entry, while the same bytes copied out first read cleanly. The copy
    /// stops at twice the tmpfs size, room for the headers of many small files that still bounds what one run can make
    /// the server hold. A container that is gone or no longer running was killed with the run.
    /// </remarks>
    internal static async Task<Collected> CollectAsync(IDockerClient docker, string containerId, SandboxOptions limits, int maxFiles, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadOutAsync(docker, containerId, limits, maxFiles, cancellationToken);
        }
        catch (DockerApiException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            return new Collected([], [$"{Confinement.OutFolder}: the container was killed with the run and removed itself, so no file was returned"]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Collected([], [$"{Confinement.OutFolder}: reading it back took past {CollectWait.TotalSeconds}s, so no file was returned"]);
        }
    }

    private static async Task<Collected> ReadOutAsync(IDockerClient docker, string containerId, SandboxOptions limits, int maxFiles, CancellationToken cancellationToken)
    {
        var execution = await docker.Exec.CreateContainerExecAsync(containerId, Confinement.Collection(limits), cancellationToken);
        var cap = limits.OutFolderBytes * 2;
        using var copied = new MemoryStream();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(CollectWait);

        using (var stream = await docker.Exec.StartContainerExecAsync(execution.ID, new ContainerExecStartParameters(), waiting.Token))
        {
            if (!await IsArchiveWithinAsync(stream, copied, cap, waiting.Token))
                return new Collected([], [$"{Confinement.OutFolder}: its archive passed {cap} bytes, so no file was returned"]);
        }

        var exitCode = await ExitCodeAsync(docker, execution.ID, CollectWait, waiting.Token);
        if (exitCode is not (0 or ChangedWhileRead))
            return new Collected([], [$"{Confinement.OutFolder}: tar could not read it back (exit {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}), so no file was returned"]);

        copied.Position = 0;

        return await RunArchive.UnpackAsync(copied, maxFiles, cancellationToken);
    }

    /// <summary>Whether the archive on the exec's standard output fitted under the cap; the copy stops at the first byte past it.</summary>
    private static async Task<bool> IsArchiveWithinAsync(MultiplexedStream stream, Stream target, long cap, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;

        for (var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
             !read.EOF;
             read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken))
        {
            if (read.Target is not MultiplexedStream.TargetStream.StandardOut)
                continue;

            copied += read.Count;
            if (copied > cap)
                return false;

            await target.WriteAsync(buffer.AsMemory(0, read.Count), cancellationToken);
        }

        return true;
    }

    private Task RemoveAsync(string containerId) => RemoveAsync(docker, containerId, logger);

    /// <summary>Kills and removes the container with its volumes; a container already gone counts as removed.</summary>
    internal static async Task RemoveAsync(IDockerClient docker, string containerId, ILogger logger)
    {
        try
        {
            await docker.Containers.RemoveContainerAsync(containerId, Confinement.Removal, CancellationToken.None);
        }
        catch (DockerApiException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            logger.LogDebug("Container {Container} was already gone or being removed", containerId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Container {Container} was not removed; it stops itself when its own clock runs out", containerId);
        }
    }

    /// <summary>Keeps a reading task that is being abandoned from surfacing its failure as an unobserved exception.</summary>
    private static void Observe(Task abandoned) =>
        abandoned.ContinueWith(static task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}

/// <summary>How one command in a container ended: its exit code, null when it was still running at its time limit, and what it printed.</summary>
internal sealed record Exec(int? ExitCode, KeptOutput Stdout, KeptOutput Stderr, TimeSpan Duration);
