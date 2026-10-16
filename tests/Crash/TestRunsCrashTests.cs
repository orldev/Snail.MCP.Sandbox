using Snail.MCP.Sandbox.Tests.Application;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Crash;

/// <summary>Test runs under load and under a daemon that fails: the node's share holds, and the project's copy never outlives the call.</summary>
public sealed class TestRunsCrashTests
{
    private const string Link = "http://minio:9000/b/project.tar.gz";

    /// <summary>Breaks the share: eight builds asked at once never take more places than the node gives test runs.</summary>
    [Fact]
    public async Task RunAsync_EightAtOnce_NeverMoreThanThePlacesRunTogether()
    {
        var gate = new TaskCompletionSource();
        var containers = new TestRunsTests.Containers { Gate = gate.Task };
        var runs = TestRunsTests.Runs(new TestRunsTests.Inputs(), new TestRunsTests.Archives(), containers, places: 2);

        var running = Enumerable.Range(0, 8).Select(_ => Task.Run(() => runs.RunAsync(Link, null, null, CancellationToken.None))).ToList();
        await Task.Delay(200);
        gate.SetResult();
        await Task.WhenAll(running);

        Assert.Equal(2, containers.MostAtOnce);
        Assert.All(running, run => Assert.Equal(TestOutcome.Passed, run.Result.Value!.Outcome));
    }

    /// <summary>Breaks the dependency: a daemon that throws mid-run still leaves no copy of the project on the node's disk.</summary>
    [Fact]
    public async Task RunAsync_ContainersThrow_StillDiscardsTheFetchedProject()
    {
        var inputs = new TestRunsTests.Inputs();
        var runs = TestRunsTests.Runs(inputs, new TestRunsTests.Archives(), new TestRunsTests.Containers { Throws = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => runs.RunAsync(Link, null, null, CancellationToken.None));

        Assert.All(inputs.Folders, fetched => Assert.Contains(inputs.Discarded, discarded => fetched.StartsWith(discarded, StringComparison.Ordinal)));
    }

    /// <summary>Breaks the place: a call cancelled while waiting for a place gives nothing back it did not take.</summary>
    [Fact]
    public async Task RunAsync_CancelledWhileWaitingForAPlace_DoesNotFreeAPlaceItNeverHeld()
    {
        var gate = new TaskCompletionSource();
        var containers = new TestRunsTests.Containers { Gate = gate.Task };
        var runs = TestRunsTests.Runs(new TestRunsTests.Inputs(), new TestRunsTests.Archives(), containers, places: 1);
        using var cancelled = new CancellationTokenSource();

        var holding = runs.RunAsync(Link, null, null, CancellationToken.None);
        var waiting = runs.RunAsync(Link, null, null, cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var third = runs.RunAsync(Link, null, null, CancellationToken.None);
        await Task.Delay(100);
        var mostWhileHeld = containers.MostAtOnce;
        gate.SetResult();
        await Task.WhenAll(holding, third);

        Assert.Equal(1, mostWhileHeld);
    }
}
