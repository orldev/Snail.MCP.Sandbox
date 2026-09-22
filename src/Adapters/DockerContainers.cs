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

    public async Task<Answer<RunOutcome>> RunAsync(string code, IReadOnlyList<InputFile> inputs, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var limits = options.Value;

        try
        {
            var system = await docker.System.GetSystemInfoAsync(cancellationToken);
            if (!system.Runtimes.ContainsKey(limits.Runtime))
                return UnknownRuntime<RunOutcome>(limits.Runtime, [.. system.Runtimes.Keys]);

            return Answer.Ok(await ConfinedAsync(code, inputs, timeout, limits, cancellationToken));
        }
        catch (DockerImageNotFoundException)
        {
            return MissingImage<RunOutcome>(limits.Image);
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
                return UnknownRuntime<Readiness>(limits.Runtime, [.. system.Runtimes.Keys]);

            await docker.Images.InspectImageAsync(limits.Image, cancellationToken);

            return Answer.Ok(new Readiness(system.ServerVersion, limits.Runtime, limits.Image));
        }
        catch (DockerImageNotFoundException)
        {
            return MissingImage<Readiness>(limits.Image);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The Docker daemon at {Endpoint} did not answer a diagnosis", limits.DockerEndpoint);

            return Answer.Failed<Readiness>(
                $"The Docker daemon at {limits.DockerEndpoint} did not answer: {exception.Message}",
                "The sandbox server cannot reach the node's Docker socket: check that the job mounts it and adds the socket's group.");
        }
    }

    /// <summary>The refusal when the daemon has no runtime of the configured name.</summary>
    /// <remarks>
    /// A refusal rather than a run under the default runtime: that run would work, look the same, and be
    /// separated from the host kernel by nothing but namespaces — the one layer gVisor exists to add.
    /// </remarks>
    private Answer<T> UnknownRuntime<T>(string runtime, IReadOnlyList<string> known)
    {
        logger.LogError("The Docker daemon has no runtime {Runtime}, only {Known}; every run is refused", runtime, string.Join(", ", known));

        return Answer.Failed<T>(
            $"The sandbox node has no {runtime} runtime, and no code runs there until it has.",
            "Calling again will not help: the sandbox node is not set up. Say so in the answer.");
    }

    private Answer<T> MissingImage<T>(string image)
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
        var execution = await docker.Exec.CreateContainerExecAsync(containerId, Confinement.Execution(limits), cancellationToken);
        var stdout = new KeptOutput(limits.OutputLimitBytes);
        var stderr = new KeptOutput(limits.OutputLimitBytes);
        var started = Stopwatch.GetTimestamp();

        using var stream = await docker.Exec.StartContainerExecAsync(execution.ID, new ContainerExecStartParameters(), cancellationToken);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var drained = DrainAsync(stream, stdout, stderr, reading.Token);
        var exitCode = await EndsWithinAsync(drained, timeout, cancellationToken)
            ? await ExitCodeAsync(execution.ID, timeout - Stopwatch.GetElapsedTime(started), cancellationToken)
            : null;
        var duration = Stopwatch.GetElapsedTime(started);

        if (exitCode is null)
        {
            await reading.CancelAsync();
            Observe(drained);
            logger.LogInformation("A run in {Container} passed its {Seconds}s and was killed", containerId, timeout.TotalSeconds);

            return new RunOutcome(null, stdout.Text, stderr.Text, duration) { TimedOut = true, IsStdoutCut = stdout.IsCut, IsStderrCut = stderr.IsCut };
        }

        var collected = await CollectAsync(containerId, limits, cancellationToken);

        return new RunOutcome(exitCode, stdout.Text, stderr.Text, duration)
        {
            IsStdoutCut = stdout.IsCut,
            IsStderrCut = stderr.IsCut,
            Outputs = collected.Files,
            Omitted = collected.Omitted
        };
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
    private async Task<int?> ExitCodeAsync(string executionId, TimeSpan left, CancellationToken cancellationToken)
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

    /// <summary>The files of /out, read from an archive copied whole into memory first.</summary>
    /// <remarks>
    /// Measured with Docker.DotNet.Enhanced 4.3.3 over the Unix socket: a TarReader on the response stream itself
    /// fails with EndOfStreamException after the first entry, for every content tried, while the same bytes copied
    /// out first read cleanly. The copy stops at twice the tmpfs size, room for the headers of many small files
    /// that still bounds what one run can make the server hold.
    /// </remarks>
    private async Task<Collected> CollectAsync(string containerId, SandboxOptions limits, CancellationToken cancellationToken)
    {
        var archive = await docker.Containers.GetArchiveFromContainerAsync(
            containerId,
            new ContainerPathStatParameters { Path = Confinement.OutFolder },
            statOnly: false,
            cancellationToken);
        var cap = limits.OutFolderBytes * 2;

        await using var content = archive.Stream ?? Stream.Null;
        using var copied = new MemoryStream();

        if (!await RunArchive.IsCopiedWithinAsync(content, copied, cap, cancellationToken))
            return new Collected([], [$"{Confinement.OutFolder}: its archive passed {cap} bytes, so no file was returned"]);

        copied.Position = 0;

        return await RunArchive.UnpackAsync(copied, limits.MaxOutputFiles, cancellationToken);
    }

    /// <summary>Kills and removes the container with its volumes; a container already gone counts as removed.</summary>
    private async Task RemoveAsync(string containerId)
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
