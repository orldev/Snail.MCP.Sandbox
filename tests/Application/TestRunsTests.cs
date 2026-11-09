using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Application;

/// <summary>A test run as the tool sees it: what is refused before anything is fetched, and what the stages come to.</summary>
public sealed class TestRunsTests
{
    private const string Link = "http://minio:9000/b/project.tar.gz";

    [Fact]
    public async Task RunAsync_ProjectGivenAsAKey_IsRefusedWithoutFetchingAnything()
    {
        var inputs = new Inputs();

        var answer = await Runs(inputs, new Archives(), new Containers()).RunAsync("tasks/7f/project.tar.gz", null, null, CancellationToken.None);

        Assert.Contains("list_objects", answer.Hint, StringComparison.Ordinal);
        Assert.Equal(0, inputs.Fetched);
    }

    [Fact]
    public async Task RunAsync_NoProject_IsRefused()
    {
        var answer = await Runs(new Inputs(), new Archives(), new Containers()).RunAsync(" ", null, null, CancellationToken.None);

        Assert.False(answer.IsSuccess);
    }

    [Fact]
    public async Task RunAsync_UnknownToolchain_IsRefusedNamingTheKnownOnes()
    {
        var answer = await Runs(new Inputs(), new Archives(), new Containers()).RunAsync(Link, "java", null, CancellationToken.None);

        Assert.Contains("dotnet, node, python", answer.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1801)]
    public async Task RunAsync_TimeoutOutsideTheLimit_IsRefusedNamingTheLimit(int seconds)
    {
        var answer = await Runs(new Inputs(), new Archives(), new Containers()).RunAsync(Link, null, seconds, CancellationToken.None);

        Assert.Contains("1 to 1800 seconds", answer.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RestoreReachedHosts_ReportsThem()
    {
        var containers = new Containers { Execution = new TestExecution([Ran(Stage.Restore, 0, string.Empty), Ran(Stage.Test, 0, "ok")], null) { Contacted = ["pypi.org"] } };

        var answer = await Runs(new Inputs(), new Archives { Paths = ["app.py", "test_app.py"] }, containers).RunAsync(Link, null, null, CancellationToken.None);

        Assert.Equal(["pypi.org"], answer.Value!.Contacted);
    }

    [Fact]
    public async Task RunAsync_NoTimeout_RunsForTheDefault()
    {
        var containers = new Containers();

        await Runs(new Inputs(), new Archives(), containers).RunAsync(Link, null, null, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(600), containers.Timeout);
    }

    [Fact]
    public async Task RunAsync_ToolchainLeftOut_IsFoundFromTheFiles()
    {
        var containers = new Containers();

        await Runs(new Inputs(), new Archives { Paths = ["app.py", "test_app.py"] }, containers).RunAsync(Link, null, null, CancellationToken.None);

        Assert.Equal(Toolchain.Python, containers.Toolchain);
    }

    [Fact]
    public async Task RunAsync_ToolchainNamed_OverridesTheFiles()
    {
        var containers = new Containers();

        await Runs(new Inputs(), new Archives { Paths = ["Shop.slnx", "package.json"] }, containers).RunAsync(Link, "node", null, CancellationToken.None);

        Assert.Equal(Toolchain.Node, containers.Toolchain);
    }

    /// <summary>An archive with nothing to build is what a project without code looks like; that is an answer, not a fault.</summary>
    [Fact]
    public async Task RunAsync_NothingBuildable_AnswersSoWithoutAContainer()
    {
        var containers = new Containers();

        var answer = await Runs(new Inputs(), new Archives { Paths = ["README.md"], Omitted = ["docs: a link"] }, containers).RunAsync(Link, null, null, CancellationToken.None);

        Assert.Equal(TestOutcome.NothingToBuild, answer.Value!.Outcome);
        Assert.Equal(["docs: a link"], answer.Value.Omitted);
        Assert.Null(containers.Toolchain);
    }

    [Fact]
    public async Task RunAsync_TestsFail_ReportsTheCountsAndTheTestStagesOutput()
    {
        var counts = new TestCounts(3, 2, 1, 0, [new TestFailure("Divides", "Expected 2")]);
        var containers = new Containers
        {
            Execution = new TestExecution([Ran(Stage.Restore, 0, "restored"), Ran(Stage.Build, 0, "built"), Ran(Stage.Test, 1, "Failed Divides")], counts)
        };

        var report = (await Runs(new Inputs(), new Archives(), containers).RunAsync(Link, null, null, CancellationToken.None)).Value!;

        Assert.Equal(TestOutcome.Failed, report.Outcome);
        Assert.Same(counts, report.Counts);
        Assert.Equal("Failed Divides", report.Output);
        Assert.Equal(TimeSpan.FromSeconds(3), report.Duration);
        Assert.Equal(Toolchain.Dotnet, report.Toolchain);
    }

    [Fact]
    public async Task RunAsync_ArchiveRefused_AnswersWithItsReasonAndRunsNothing()
    {
        var containers = new Containers();

        var answer = await Runs(new Inputs(), new Archives { Refusal = "The project has more than 20000 files." }, containers).RunAsync(Link, null, null, CancellationToken.None);

        Assert.Equal("The project has more than 20000 files.", answer.Error);
        Assert.Null(containers.Toolchain);
    }

    [Fact]
    public async Task RunAsync_FetchFails_AnswersWithItsReason()
    {
        var answer = await Runs(new Inputs { Failure = "project.tar.gz: the link answered 403." }, new Archives(), new Containers()).RunAsync(Link, null, null, CancellationToken.None);

        Assert.Equal("project.tar.gz: the link answered 403.", answer.Error);
    }

    [Fact]
    public async Task RunAsync_ContainersRefuse_PassesTheRefusalOn()
    {
        var containers = new Containers { Refusal = "The sandbox node has no runsc runtime." };

        var answer = await Runs(new Inputs(), new Archives(), containers).RunAsync(Link, null, null, CancellationToken.None);

        Assert.Equal("The sandbox node has no runsc runtime.", answer.Error);
    }

    /// <summary>
    /// A failure of a stage is handed on whole: carried by hand into a failure of the report, its error and hint were kept
    /// and its type was lost, so a job of the run ended "failed" whatever kind the stage had named.
    /// </summary>
    [Theory]
    [InlineData("fetch")]
    [InlineData("repack")]
    [InlineData("test")]
    public async Task RunAsync_StageFailsWithAType_PassesTheTypeOn(string stage)
    {
        var inputs = new Inputs { Failure = stage == "fetch" ? "refused" : null, FailureType = "unreachable" };
        var archives = new Archives { Refusal = stage == "repack" ? "refused" : null, RefusalType = "unreachable" };
        var containers = new Containers { Refusal = stage == "test" ? "refused" : null, RefusalType = "unreachable" };

        var answer = await Runs(inputs, archives, containers).RunAsync(Link, null, null, CancellationToken.None);

        Assert.Equal("refused", answer.Error);
        Assert.Equal("a hint", answer.Hint);
        Assert.Equal("unreachable", answer.Type);
    }

    internal static TestRuns Runs(Inputs inputs, Archives archives, Containers containers, int places = 2, int queued = 4) =>
        new(inputs, archives, containers, Options.Create(new SandboxOptions { MaxConcurrentTestRuns = places, MaxQueuedTestRuns = queued }));

    internal static StageRun Ran(Stage stage, int? exitCode, string output) => new(stage, exitCode, output, false, TimeSpan.FromSeconds(1));

    internal sealed class Inputs : IInputs
    {
        public string? Failure { get; init; }

        public string? FailureType { get; init; }

        public int Fetched { get; private set; }

        public List<string> Folders { get; } = [];

        public List<string> Discarded { get; } = [];

        public Task<Answer<IReadOnlyList<InputFile>>> FetchAsync(IReadOnlyList<Uri> links, string folder, CancellationToken cancellationToken)
        {
            Fetched++;
            Folders.Add(folder);

            return Task.FromResult(Failure is null
                ? Answer.Ok<IReadOnlyList<InputFile>>([.. links.Select(link => new InputFile(link.Segments[^1], Path.Combine(folder, link.Segments[^1]), 1))])
                : Answer.Failed<IReadOnlyList<InputFile>>(Failure, "a hint", FailureType));
        }

        public void Discard(string folder)
        {
            lock (Discarded)
                Discarded.Add(folder);
        }
    }

    internal sealed class Archives : IProjectArchives
    {
        public IReadOnlyList<string> Paths { get; init; } = ["Shop.slnx", "src/Shop/Shop.csproj"];

        public IReadOnlyList<string> Omitted { get; init; } = [];

        public string? Refusal { get; init; }

        public string? RefusalType { get; init; }

        public Task<Answer<PackedProject>> RepackAsync(string archive, string folder, CancellationToken cancellationToken) =>
            Task.FromResult(Refusal is null
                ? Answer.Ok(new PackedProject(Path.Combine(folder, "project.tar"), Paths, Omitted))
                : Answer.Failed<PackedProject>(Refusal, "a hint", RefusalType));
    }

    internal sealed class Containers : ITestContainers
    {
        private int _running;

        public TestExecution Execution { get; init; } = new([Ran(Stage.Restore, 0, string.Empty), Ran(Stage.Build, 0, string.Empty), Ran(Stage.Test, 0, "Passed!")], new TestCounts(1, 1, 0, 0, []));

        public string? Refusal { get; init; }

        public string? RefusalType { get; init; }

        public bool Throws { get; init; }

        public Task Gate { get; init; } = Task.CompletedTask;

        public Toolchain? Toolchain { get; private set; }

        public TimeSpan Timeout { get; private set; }

        public int MostAtOnce { get; private set; }

        public async Task<Answer<TestExecution>> TestAsync(PackedProject project, Toolchain toolchain, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var running = Interlocked.Increment(ref _running);
            lock (this)
                MostAtOnce = Math.Max(MostAtOnce, running);

            Toolchain = toolchain;
            Timeout = timeout;

            try
            {
                if (Throws)
                    throw new InvalidOperationException("the daemon went away");

                await Gate;

                return Refusal is null ? Answer.Ok(Execution) : Answer.Failed<TestExecution>(Refusal, "a hint", RefusalType);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }
}
