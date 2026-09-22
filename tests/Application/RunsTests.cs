using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Application;

public sealed class RunsTests
{
    private static readonly RunOutcome Finished = new(0, "done\n", string.Empty, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task RunAsync_NoCode_IsRefusedBeforeAnythingRuns()
    {
        var containers = new Containers();

        var answer = await Runs(new Inputs(), containers).RunAsync("  ", null, null, CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Equal(0, containers.Started);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public async Task RunAsync_TimeoutOutsideTheLimit_IsRefusedNamingTheLimit(int seconds)
    {
        var answer = await Runs(new Inputs(), new Containers()).RunAsync("print(1)", null, seconds, CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("1 to 300 seconds", answer.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoTimeout_RunsForTheDefault()
    {
        var containers = new Containers();

        await Runs(new Inputs(), containers).RunAsync("print(1)", null, null, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(60), containers.Timeout);
    }

    /// <summary>A key passed straight through, when the platform did not sign it, is the usual way a file arrives as a name.</summary>
    [Fact]
    public async Task RunAsync_FileGivenAsAKey_IsRefusedWithoutFetchingAnything()
    {
        var inputs = new Inputs();

        var answer = await Runs(inputs, new Containers()).RunAsync("print(1)", ["tasks/7f/attachments/sales.csv"], null, CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("list_objects", answer.Hint, StringComparison.Ordinal);
        Assert.Equal(0, inputs.Fetched);
    }

    [Fact]
    public async Task RunAsync_FetchFails_AnswersWithItsReasonAndRunsNothing()
    {
        var containers = new Containers();
        var inputs = new Inputs { Failure = "sales.csv: the link answered 403." };

        var answer = await Runs(inputs, containers).RunAsync("print(1)", ["http://minio:9000/b/sales.csv"], null, CancellationToken.None);

        Assert.Equal("sales.csv: the link answered 403.", answer.Error);
        Assert.Equal(0, containers.Started);
    }

    /// <summary>The downloaded files are a copy of a task's data on the node's disk; they must not outlive the call, even a failed one.</summary>
    [Fact]
    public async Task RunAsync_ContainerThrows_StillDiscardsTheFetchedFiles()
    {
        var inputs = new Inputs();
        var containers = new Containers { Throws = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runs(inputs, containers).RunAsync("print(1)", ["http://minio:9000/b/sales.csv"], null, CancellationToken.None));

        Assert.Equal(inputs.Folders, inputs.Discarded);
        Assert.Single(inputs.Discarded);
    }

    [Fact]
    public async Task RunAsync_AllPlacesTaken_WaitsForOneToFree()
    {
        var gate = new TaskCompletionSource();
        var containers = new Containers { Gate = gate.Task };
        var runs = Runs(new Inputs(), containers, maxConcurrentRuns: 1);

        var first = runs.RunAsync("print(1)", null, null, CancellationToken.None);
        var second = runs.RunAsync("print(2)", null, null, CancellationToken.None);
        await Task.Delay(100);
        var startedWhileFull = containers.Started;
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, startedWhileFull);
        Assert.Equal(2, containers.Started);
    }

    private static Runs Runs(Inputs inputs, Containers containers, int maxConcurrentRuns = 4) =>
        new(inputs, containers, Options.Create(new SandboxOptions { MaxConcurrentRuns = maxConcurrentRuns }));

    private sealed class Inputs : IInputs
    {
        public string? Failure { get; init; }

        public int Fetched { get; private set; }

        public List<string> Folders { get; } = [];

        public List<string> Discarded { get; } = [];

        public Task<Answer<IReadOnlyList<InputFile>>> FetchAsync(IReadOnlyList<Uri> links, string folder, CancellationToken cancellationToken)
        {
            Fetched++;
            Folders.Add(folder);

            return Task.FromResult(Failure is null
                ? Answer.Ok<IReadOnlyList<InputFile>>([.. links.Select(link => new InputFile(link.Segments[^1], Path.Combine(folder, link.Segments[^1]), 1))])
                : Answer.Failed<IReadOnlyList<InputFile>>(Failure));
        }

        public void Discard(string folder) => Discarded.Add(folder);
    }

    private sealed class Containers : IContainers
    {
        private int _started;

        public bool Throws { get; init; }

        public Task Gate { get; init; } = Task.CompletedTask;

        public int Started => _started;

        public TimeSpan Timeout { get; private set; }

        public async Task<Answer<RunOutcome>> RunAsync(string code, IReadOnlyList<InputFile> inputs, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);
            Timeout = timeout;

            if (Throws)
                throw new InvalidOperationException("the daemon went away");

            await Gate;

            return Answer.Ok(Finished);
        }

        public Task<Answer<Readiness>> DiagnoseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Answer.Ok(new Readiness("29.1.0", "runsc", "snail/sandbox-python:latest")));
    }
}
