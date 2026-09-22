using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Application;

/// <summary>One call of the tool: the request checked, its files fetched, the code run, the files forgotten.</summary>
/// <remarks>
/// Everything a model can get wrong is answered before a byte is downloaded or a container exists: no code, a
/// timeout outside the limit, a file given as something other than a link. A key passed straight through, which
/// happens when the platform did not sign it, is the common case of the last one, and the hint says so.
/// </remarks>
public sealed class Runs(IInputs inputs, IContainers containers, IOptions<SandboxOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _places = new(options.Value.MaxConcurrentRuns);

    public async Task<Answer<RunOutcome>> RunAsync(string code, IReadOnlyList<string>? files, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        var limits = options.Value;
        var seconds = timeoutSeconds ?? limits.DefaultTimeoutSeconds;
        var wanted = files ?? [];

        if (string.IsNullOrWhiteSpace(code))
            return Answer.Failed<RunOutcome>("There is no code to run.", "Pass the whole program in code; nothing from an earlier call is kept.");

        if (seconds <= 0 || seconds > limits.MaxTimeoutSeconds)
            return Answer.Failed<RunOutcome>(
                $"timeoutSeconds was {seconds}; a run may take from 1 to {limits.MaxTimeoutSeconds} seconds.",
                "Do less in one call when the work needs more than that.");

        if (wanted.FirstOrDefault(file => !Uri.TryCreate(file, UriKind.Absolute, out _)) is { } unsigned)
            return Answer.Failed<RunOutcome>(
                $"The file {unsigned} reached the sandbox as a name, not as a link, so it cannot be read.",
                "Pass the keys of this task's files exactly as list_objects shows them; the platform signs those. A path on your own disk does not exist here.");

        await _places.WaitAsync(cancellationToken);
        var folder = Path.Combine(limits.WorkRoot, Guid.NewGuid().ToString("N"));

        try
        {
            var fetched = await inputs.FetchAsync([.. wanted.Select(file => new Uri(file))], folder, cancellationToken);
            if (fetched.Value is not { } placed)
                return Answer.Failed<RunOutcome>(fetched.Error ?? "The files were not fetched.", fetched.Hint);

            return await containers.RunAsync(code, placed, TimeSpan.FromSeconds(seconds), cancellationToken);
        }
        finally
        {
            inputs.Discard(folder);
            _places.Release();
        }
    }

    public void Dispose() => _places.Dispose();
}
