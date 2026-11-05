using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Application;

/// <summary>One test run of a project: the request checked, the archive fetched and rewritten, the stages run, the files forgotten.</summary>
/// <remarks>
/// Everything a caller can get wrong is answered before a byte is downloaded: no project, a key passed where a link
/// was due, a toolchain this sandbox does not have, a timeout outside the limit. An archive with nothing to build is
/// an answer rather than a refusal — it is what a project without code looks like, and the caller says so to the human.
/// Test runs take places of their own, apart from program runs, because one build holds four times the memory.
/// The archive is fetched into a folder of its own under the run's: the rewritten one is named project.tar, and a
/// key may well end in the same name.
/// </remarks>
public sealed class TestRuns(IInputs inputs, IProjectArchives archives, ITestContainers containers, IOptions<SandboxOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _places = new(options.Value.MaxConcurrentTestRuns);

    private int _asked;

    /// <summary>What the test run found out, or why it could not run: a request refused before anything ran, the archive not fetched or not readable, the node full.</summary>
    public async Task<Answer<TestReport>> RunAsync(string project, string? toolchain, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        var limits = options.Value;
        var seconds = timeoutSeconds ?? limits.DefaultTestTimeoutSeconds;
        var named = string.IsNullOrWhiteSpace(toolchain) ? null : Toolchains.Named(toolchain);

        if (string.IsNullOrWhiteSpace(project))
            return Answer.Failed<TestReport>("There is no project to test.", "Pass the key of the project's archive as list_objects shows it.");

        if (!Uri.TryCreate(project, UriKind.Absolute, out var link))
            return Answer.Failed<TestReport>(
                $"The project {project} reached the sandbox as a name, not as a link, so it cannot be read.",
                "Pass the key of this task's project archive exactly as list_objects shows it; the platform signs it. A path on your own disk does not exist here.");

        if (!string.IsNullOrWhiteSpace(toolchain) && named is null)
            return Answer.Failed<TestReport>(
                $"There is no toolchain called {toolchain}.",
                $"Name one of {string.Join(", ", Toolchains.Names)}, or leave it out to have it found from the files.");

        if (seconds <= 0 || seconds > limits.MaxTestTimeoutSeconds)
            return Answer.Failed<TestReport>(
                $"timeoutSeconds was {seconds}; a test run may take from 1 to {limits.MaxTestTimeoutSeconds} seconds.",
                "Run fewer tests in one call when the project needs more than that.");

        if (Interlocked.Increment(ref _asked) > limits.MaxConcurrentTestRuns + limits.MaxQueuedTestRuns)
        {
            Interlocked.Decrement(ref _asked);

            return Answer.Failed<TestReport>(
                $"The sandbox node is full: {limits.MaxConcurrentTestRuns} test runs are going and {limits.MaxQueuedTestRuns} are waiting for a place.",
                "Say so in the answer rather than calling again at once; a place frees when a test run ends.");
        }

        try
        {
            return await PlacedAsync(link, named, TimeSpan.FromSeconds(seconds), limits, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _asked);
        }
    }

    /// <summary>The test run once it has a place: in a folder of its own, forgotten when it ends.</summary>
    private async Task<Answer<TestReport>> PlacedAsync(Uri link, Toolchain? named, TimeSpan timeout, SandboxOptions limits, CancellationToken cancellationToken)
    {
        await _places.WaitAsync(cancellationToken);
        var folder = Path.Combine(limits.WorkRoot, Guid.NewGuid().ToString("N"));

        try
        {
            return await TestedAsync(link, named, timeout, folder, cancellationToken);
        }
        finally
        {
            inputs.Discard(folder);
            _places.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _places.Dispose();

    private async Task<Answer<TestReport>> TestedAsync(Uri link, Toolchain? named, TimeSpan timeout, string folder, CancellationToken cancellationToken)
    {
        var fetched = await inputs.FetchAsync([link], Path.Combine(folder, "in"), cancellationToken);
        if (fetched.Value is not [var archive])
            return Answer.Failed<TestReport>(fetched.Error ?? "The project was not fetched.", fetched.Hint);

        var packed = await archives.RepackAsync(archive.Path, folder, cancellationToken);
        if (packed.Value is not { } project)
            return Answer.Failed<TestReport>(packed.Error ?? "The project archive was not read.", packed.Hint);

        if ((named ?? Toolchains.Of(project.Paths)) is not { } toolchain)
            return Answer.Ok(new TestReport(null, TestOutcome.NothingToBuild, TimeSpan.Zero) { Omitted = project.Omitted });

        var executed = await containers.TestAsync(project, toolchain, timeout, cancellationToken);
        if (executed.Value is not { } execution)
            return Answer.Failed<TestReport>(executed.Error ?? "The tests did not run.", executed.Hint);

        var deciding = Toolchains.Deciding(execution.Stages);

        return Answer.Ok(new TestReport(toolchain, Toolchains.Verdict(toolchain, execution.Stages, execution.Counts), Total(execution.Stages))
        {
            Counts = execution.Counts,
            Output = deciding.Output,
            IsOutputCut = deciding.IsOutputCut,
            Omitted = project.Omitted,
            Contacted = execution.Contacted
        });
    }

    private static TimeSpan Total(IReadOnlyList<StageRun> stages) =>
        stages.Aggregate(TimeSpan.Zero, (sum, stage) => sum + stage.Duration);
}
