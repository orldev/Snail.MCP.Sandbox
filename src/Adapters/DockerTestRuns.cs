using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>Restores, builds and tests a project in one container on the node's Docker daemon, and removes it afterwards.</summary>
/// <remarks>
/// The stages run one after another as execs of the same container, so the build finds what the restore fetched and
/// the tests find what the build wrote. A stage that fails stops the run: a build after a failed restore only repeats
/// the restore's error in worse words. The network the restore used is disconnected before the build and checked
/// gone; a container still attached to one is removed without building anything.
/// </remarks>
public sealed class DockerTestRuns(IDockerClient docker, IOptions<SandboxOptions> options, ILogger<DockerTestRuns> logger, RestoreProxy? proxy = null) : ITestContainers
{
    /// <summary>Most report files read back from one run: one per test project, so more than a run of programs returns.</summary>
    internal const int MaxReports = 200;

    private readonly ConcurrentDictionary<Toolchain, bool> _handedOver = new();

    /// <inheritdoc />
    public async Task<Answer<TestExecution>> TestAsync(PackedProject project, Toolchain toolchain, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var limits = options.Value;

        try
        {
            var system = await DockerContainers.SystemAsync(docker, cancellationToken);
            if (!system.Runtimes.ContainsKey(limits.Runtime))
                return DockerContainers.UnknownRuntime<TestExecution>(logger, limits.Runtime, [.. system.Runtimes.Keys]);

            await HandOverAsync(toolchain, limits, cancellationToken);

            return Answer.Ok(await TestedAsync(project, toolchain, timeout, limits, cancellationToken));
        }
        catch (DockerImageNotFoundException)
        {
            return DockerContainers.MissingImage<TestExecution>(logger, TestCommands.Image(toolchain, limits));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return DockerContainers.NotAnswered<TestExecution>(logger, exception, limits.DockerEndpoint);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.TestRunNotCompleted(logger, exception, limits.DockerEndpoint);

            return Answer.Failed(
                $"The sandbox could not run the tests: {exception.Message}",
                "This is the sandbox failing, not the project. Call once more; if it fails again, say so in the answer.");
        }
    }

    /// <summary>Gives the toolchain's package volume to the run user: once per volume, not once per process.</summary>
    /// <remarks>
    /// A volume no container holds is what docker volume prune removes, and the one made in its place is root's again;
    /// a restore that cannot write to it fails worded as the project's own packages being wrong, until the server
    /// restarts. So a volume the process remembers handing over is looked at before each run, and handed over again
    /// when it is gone.
    /// </remarks>
    private async Task HandOverAsync(Toolchain toolchain, SandboxOptions limits, CancellationToken cancellationToken)
    {
        if (_handedOver.ContainsKey(toolchain) && await ExistsAsync(TestCommands.CacheVolume(toolchain, limits), cancellationToken))
            return;

        _handedOver.TryRemove(toolchain, out _);
        var container = await docker.Containers.CreateContainerAsync(TestCommands.Handover(toolchain, limits), cancellationToken);

        try
        {
            await docker.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), cancellationToken);
            var waited = await docker.Containers.WaitContainerAsync(container.ID, cancellationToken);

            if (waited.StatusCode != 0)
                throw new InvalidOperationException($"the package volume of {toolchain} was not handed to the run user; chown exited with {waited.StatusCode}");
        }
        finally
        {
            await DockerContainers.RemoveAsync(docker, container.ID, logger);
        }

        _handedOver[toolchain] = true;
    }

    private async Task<bool> ExistsAsync(string volume, CancellationToken cancellationToken)
    {
        try
        {
            await docker.Volumes.InspectAsync(volume, cancellationToken);

            return true;
        }
        catch (DockerApiException exception) when (exception.StatusCode is HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<TestExecution> TestedAsync(PackedProject project, Toolchain toolchain, TimeSpan timeout, SandboxOptions limits, CancellationToken cancellationToken)
    {
        var container = await docker.Containers.CreateContainerAsync(TestCommands.Container(toolchain, limits, timeout), cancellationToken);
        using RestoreWatch? watch = proxy?.Watch();

        try
        {
            await using (var archive = File.OpenRead(project.Archive))
                await docker.Containers.ExtractArchiveToContainerAsync(container.ID, new CopyToContainerParameters { Path = Confinement.Root }, archive, cancellationToken);

            await docker.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), cancellationToken);

            var stages = await StagesAsync(container.ID, project, toolchain, timeout, limits, watch?.Proxy, cancellationToken);
            var counts = stages[^1] is { Stage: Stage.Test, ExitCode: not null }
                ? TestReports.Read((await DockerContainers.CollectAsync(docker, container.ID, limits, MaxReports, cancellationToken)).Files)
                : null;

            return new TestExecution(stages, counts) { Contacted = watch?.Hosts };
        }
        finally
        {
            await DockerContainers.RemoveAsync(docker, container.ID, logger);
        }
    }

    /// <summary>Runs the stages in order until one fails or the time is up; the network goes once, right after the restore.</summary>
    private async Task<IReadOnlyList<StageRun>> StagesAsync(
        string containerId,
        PackedProject project,
        Toolchain toolchain,
        TimeSpan timeout,
        SandboxOptions limits,
        string? restoreProxy,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        List<StageRun> stages = [];

        foreach (var stage in TestCommands.Stages(toolchain))
        {
            if (stages is [{ Stage: Stage.Restore }])
                await OfflineAsync(containerId, limits, cancellationToken);

            var left = timeout - Stopwatch.GetElapsedTime(started);
            if (left <= TimeSpan.Zero)
            {
                stages.Add(new StageRun(stage, null, string.Empty, false, TimeSpan.Zero));
                break;
            }

            var exec = await DockerContainers.ExecAsync(docker, containerId, TestCommands.Execution(toolchain, stage, project.Paths, limits, restoreProxy), left, limits.OutputLimitBytes, cancellationToken);
            stages.Add(Ran(stage, exec));

            if (exec.ExitCode != 0)
                break;
        }

        return stages;
    }

    /// <summary>Takes the container off the restore network and makes sure it is attached to none.</summary>
    private async Task OfflineAsync(string containerId, SandboxOptions limits, CancellationToken cancellationToken)
    {
        if (limits.RestoreNetwork is not "none")
            await docker.Networks.DisconnectNetworkAsync(limits.RestoreNetwork, new NetworkDisconnectParameters { Container = containerId, Force = true }, cancellationToken);

        var inspected = await docker.Containers.InspectContainerAsync(containerId, cancellationToken);
        var attached = inspected.NetworkSettings?.Networks?.Keys.Where(network => network is not "none").ToList() ?? [];

        if (attached.Count > 0)
            throw new InvalidOperationException($"the container was still attached to {string.Join(", ", attached)} after the restore, so nothing was built");
    }

    private static StageRun Ran(Stage stage, Exec exec)
    {
        var output = exec.Stderr.Text.Length == 0 ? exec.Stdout.Text : $"{exec.Stdout.Text}\n{exec.Stderr.Text}";

        return new StageRun(stage, exec.ExitCode, output, exec.Stdout.IsCut || exec.Stderr.IsCut, exec.Duration);
    }
}
