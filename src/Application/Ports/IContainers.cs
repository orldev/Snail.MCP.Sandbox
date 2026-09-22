namespace Snail.MCP.Sandbox.Application.Ports;

/// <summary>Runs code in a throwaway container confined by the server's limits, and removes it afterwards.</summary>
public interface IContainers
{
    /// <summary>What the run did, or why it could not run at all — an unknown runtime, a missing image, an unreachable daemon.</summary>
    Task<Answer<RunOutcome>> RunAsync(string code, IReadOnlyList<InputFile> inputs, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Whether a run could start at all: the daemon answers, knows the runtime and holds the image — without starting anything.</summary>
    Task<Answer<Readiness>> DiagnoseAsync(CancellationToken cancellationToken);
}
