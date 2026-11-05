using Docker.DotNet;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>Removes, at start, the run containers an earlier process of this server left behind.</summary>
/// <remarks>
/// A server killed between creating a container and starting it leaves one that never runs its own clock out,
/// holding a copy of a task's files on the node's disk. One server is expected per daemon, so every container
/// carrying the label at start belongs to a process that no longer exists. A daemon that does not answer, or not
/// within <see cref="DockerContainers.AnswerWait"/>, is logged and left: the server still starts, and the next start
/// sweeps again. Without the deadline a hung daemon held the host's start for the client's own timeout, thirty-one
/// minutes (chaos test, 30.10.2026).
/// </remarks>
public sealed class LeftoverContainers(IDockerClient docker, ILogger<LeftoverContainers> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(DockerContainers.AnswerWait);

        try
        {
            var leftovers = await docker.Containers.ListContainersAsync(Confinement.Leftovers, waiting.Token).WaitAsync(waiting.Token);

            foreach (var container in leftovers)
                await docker.Containers.RemoveContainerAsync(container.ID, Confinement.Removal, waiting.Token).WaitAsync(waiting.Token);

            if (leftovers.Count > 0)
                Log.LeftoversRemoved(logger, leftovers.Count);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            Log.LeftoversNotSwept(logger, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.LeftoversNotSwept(logger, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
