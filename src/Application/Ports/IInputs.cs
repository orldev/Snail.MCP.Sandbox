namespace Snail.MCP.Sandbox.Application.Ports;

/// <summary>Brings the files a run reads onto the node, into a folder of that run's own.</summary>
public interface IInputs
{
    /// <summary>The downloaded files, or why none were: a forbidden host, a file past its limit, a link that failed.</summary>
    Task<Answer<IReadOnlyList<InputFile>>> FetchAsync(IReadOnlyList<Uri> links, string folder, CancellationToken cancellationToken);

    /// <summary>Removes the folder of one run and everything fetched into it, including a download that stopped halfway.</summary>
    void Discard(string folder);
}
