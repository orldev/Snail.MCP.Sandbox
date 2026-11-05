using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>The node's places against a client that no longer has to wait for one.</summary>
public sealed class RunsChaosTests
{
    /// <summary>
    /// Breaks the backpressure: a call with wait 0 returns in milliseconds, so one client can start a hundred runs in a second.
    /// Four run; ninety-six wait for a place, each holding its code and its links, and the last of them executes hours later
    /// whether or not anyone still wants it — nothing refuses a run past the queue, and nothing cancels one. A caller past the
    /// node's share has to be answered now, with a refusal that says so.
    /// </summary>
    [Fact]
    public async Task RunAsync_HundredRunsStartedAtOnce_AnswersTheLastOneNowRatherThanQueueingItForHours()
    {
        var holding = new TaskCompletionSource();
        var containers = new Containers(holding.Task);
        using var runs = new Runs(new Inputs(), containers, Options.Create(new SandboxOptions { MaxConcurrentRuns = 4 }));

        var started = Enumerable.Range(0, 100).Select(_ => runs.RunAsync("print(1)", null, null, CancellationToken.None)).ToList();
        var last = await Task.WhenAny(started[^1], Task.Delay(TimeSpan.FromSeconds(1)));

        holding.SetResult();
        Assert.Same(started[^1], last);
        Assert.False((await started[^1]).IsSuccess);
    }

    private sealed class Inputs : IInputs
    {
        public Task<Answer<IReadOnlyList<InputFile>>> FetchAsync(IReadOnlyList<Uri> links, string folder, CancellationToken cancellationToken) =>
            Task.FromResult(Answer.Ok<IReadOnlyList<InputFile>>([]));

        public void Discard(string folder)
        {
        }
    }

    private sealed class Containers(Task gate) : IContainers
    {
        public async Task<Answer<RunOutcome>> RunAsync(string code, IReadOnlyList<InputFile> inputs, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await gate;

            return Answer.Ok(new RunOutcome(0, string.Empty, string.Empty, TimeSpan.Zero));
        }

        public Task<Answer<Readiness>> DiagnoseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Answer.Ok(new Readiness("29.1.0", "runsc", "snail/sandbox-python:latest")));
    }
}
