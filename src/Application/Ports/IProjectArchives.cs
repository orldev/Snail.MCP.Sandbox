namespace Snail.MCP.Sandbox.Application.Ports;

/// <summary>Turns the archive a project arrived in into the one its container is filled from.</summary>
public interface IProjectArchives
{
    /// <summary>The project rewritten into <paramref name="folder"/>, or why it is refused: unreadable, too large, climbing out of itself.</summary>
    Task<Answer<PackedProject>> RepackAsync(string archive, string folder, CancellationToken cancellationToken);
}
