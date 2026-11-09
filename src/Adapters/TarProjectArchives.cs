using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>Rewrites a project's tar or tar.gz under the server's limits, owned by the run user.</summary>
public sealed class TarProjectArchives(IOptions<SandboxOptions> options) : IProjectArchives
{
    /// <inheritdoc />
    public Task<Answer<PackedProject>> RepackAsync(string archive, string folder, CancellationToken cancellationToken)
    {
        var limits = options.Value;

        return ProjectArchive.RepackAsync(
            archive,
            Path.Combine(folder, "project.tar"),
            Owner.Of(limits.User),
            new ProjectLimits(limits.MaxProjectBytes, limits.MaxProjectFiles),
            cancellationToken);
    }
}
