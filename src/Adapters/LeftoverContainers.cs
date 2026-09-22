using Docker.DotNet;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>Removes, at start, the run containers an earlier process of this server left behind.</summary>
/// <remarks>
/// A server killed between creating a container and starting it leaves one that never runs its own clock out,
/// holding a copy of a task's files on the node's disk. One server is expected per daemon, so every container
/// carrying the label at start belongs to a process that no longer exists. A daemon that does not answer yet
/// is logged and left: the server still starts, and the next start sweeps again.
/// </remarks>
public sealed class LeftoverContainers(IDockerClient docker, ILogger<LeftoverContainers> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var leftovers = await docker.Containers.ListContainersAsync(Confinement.Leftovers, cancellationToken);

            foreach (var container in leftovers)
                await docker.Containers.RemoveContainerAsync(container.ID, Confinement.Removal, cancellationToken);

            if (leftovers.Count > 0)
                logger.LogWarning("Removed {Count} run containers left by an earlier process of this server", leftovers.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Leftover run containers were not swept; the Docker daemon did not answer");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
