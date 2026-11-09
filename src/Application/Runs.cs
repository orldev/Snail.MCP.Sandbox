using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Application;

/// <summary>One call of the tool: the request checked, its files fetched, the code run, the files forgotten.</summary>
/// <remarks>
/// Everything a model can get wrong is answered before a byte is downloaded or a container exists: no code, a
/// timeout outside the limit, a file given as something other than a link. A key passed straight through, which
/// happens when the platform did not sign it, is the common case of the last one, and the hint says so. A call past
/// the node's places and the runs allowed to wait for one is refused at once rather than queued for hours.
/// </remarks>
public sealed class Runs(IInputs inputs, IContainers containers, IOptions<SandboxOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _places = new(options.Value.MaxConcurrentRuns);

    private int _asked;

    /// <summary>What the run did, or why it could not: a request refused before anything ran, a file not fetched, the node full.</summary>
    public async Task<Answer<RunOutcome>> RunAsync(string code, IReadOnlyList<string>? files, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        var limits = options.Value;
        var seconds = timeoutSeconds ?? limits.DefaultTimeoutSeconds;
        var wanted = files ?? [];

        if (string.IsNullOrWhiteSpace(code))
            return Answer.Failed("There is no code to run.", "Pass the whole program in code; nothing from an earlier call is kept.", FailureTypes.Arguments);

        if (seconds <= 0 || seconds > limits.MaxTimeoutSeconds)
            return Answer.Failed(
                $"timeoutSeconds was {seconds}; a run may take from 1 to {limits.MaxTimeoutSeconds} seconds.",
                "Do less in one call when the work needs more than that.",
                FailureTypes.Arguments);

        if (wanted.FirstOrDefault(file => !Uri.TryCreate(file, UriKind.Absolute, out _)) is { } unsigned)
            return Answer.Failed(
                $"The file {unsigned} reached the sandbox as a name, not as a link, so it cannot be read.",
                "Pass the keys of this task's files exactly as list_objects shows them; the platform signs those. A path on your own disk does not exist here.",
                FailureTypes.Arguments);

        if (Interlocked.Increment(ref _asked) > limits.MaxConcurrentRuns + limits.MaxQueuedRuns)
        {
            Interlocked.Decrement(ref _asked);

            return Answer.Failed(
                $"The sandbox node is full: {limits.MaxConcurrentRuns} runs are going and {limits.MaxQueuedRuns} are waiting for a place.",
                "Say so in the answer rather than calling again at once; a place frees when a run ends.",
                FailureTypes.Busy);
        }

        try
        {
            return await PlacedAsync(code, wanted, TimeSpan.FromSeconds(seconds), limits, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _asked);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _places.Dispose();

    /// <summary>The run once it has a place: its files fetched into a folder of its own, the code run, the folder forgotten.</summary>
    /// <remarks>A failure to fetch is handed on whole (<c>As&lt;T&gt;</c>): carried by hand, its error and hint were kept and its type was lost.</remarks>
    private async Task<Answer<RunOutcome>> PlacedAsync(string code, IReadOnlyList<string> wanted, TimeSpan timeout, SandboxOptions limits, CancellationToken cancellationToken)
    {
        await _places.WaitAsync(cancellationToken);
        var folder = Path.Combine(limits.WorkRoot, Guid.NewGuid().ToString("N"));

        try
        {
            var fetched = await inputs.FetchAsync([.. wanted.Select(file => new Uri(file))], folder, cancellationToken);
            if (!fetched.TryGet(out var placed))
                return fetched.As<RunOutcome>();

            return await containers.RunAsync(code, placed, timeout, cancellationToken);
        }
        finally
        {
            inputs.Discard(folder);
            _places.Release();
        }
    }
}
