using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Domain;

/// <summary>Which toolchain a project is built with, and what its stages add up to.</summary>
public sealed class ToolchainsTests
{
    [Theory]
    [InlineData("Shop.slnx|src/Shop/Shop.csproj|web/package.json", Toolchain.Dotnet)]
    [InlineData("src/Lib/Lib.fsproj", Toolchain.Dotnet)]
    [InlineData("package.json|scripts/build.py", Toolchain.Node)]
    [InlineData("src/cache.py|test_cache.py", Toolchain.Python)]
    [InlineData("pyproject.toml", Toolchain.Python)]
    public void Of_ProjectFiles_PicksTheToolchainThatBuildsTheWhole(string files, Toolchain expected) =>
        Assert.Equal(expected, Toolchains.Of(files.Split('|')));

    /// <summary>A package.json deep in a Python project is a tool of that project, not a Node project.</summary>
    [Fact]
    public void Of_PackageJsonBelowTheRoot_DoesNotMakeItANodeProject() =>
        Assert.Equal(Toolchain.Python, Toolchains.Of(["docs/site/package.json", "app.py"]));

    [Fact]
    public void Of_NothingBuildable_IsNull() =>
        Assert.Null(Toolchains.Of(["README.md", "notes/plan.txt"]));

    [Theory]
    [InlineData("dotnet", Toolchain.Dotnet)]
    [InlineData(" Python ", Toolchain.Python)]
    [InlineData("NODE", Toolchain.Node)]
    public void Named_KnownName_IsThatToolchain(string name, Toolchain expected) => Assert.Equal(expected, Toolchains.Named(name));

    /// <summary>Enum parsing would take "1" for Python; a caller naming a number meant nothing this sandbox has.</summary>
    [Theory]
    [InlineData("1")]
    [InlineData("java")]
    [InlineData("")]
    public void Named_UnknownName_IsNull(string name) => Assert.Null(Toolchains.Named(name));

    [Fact]
    public void Names_EveryToolchain_IsNamed() =>
        Assert.Equal(Enum.GetValues<Toolchain>().Order(), Toolchains.Names.Select(name => Toolchains.Named(name)!.Value).Order());

    /// <summary>Two solutions at the root stop dotnet with MSB1011, so one is chosen: the new format first.</summary>
    [Fact]
    public void DotnetTarget_BothSolutionFormatsAtTheRoot_IsTheSlnx() =>
        Assert.Equal("Shop.slnx", Toolchains.DotnetTarget(["Shop.sln", "Shop.slnx", "src/Shop/Shop.csproj"]));

    [Fact]
    public void DotnetTarget_OnlyAProjectAtTheRoot_IsThatProject() =>
        Assert.Equal("Tool.csproj", Toolchains.DotnetTarget(["Tool.csproj", "Program.cs"]));

    [Fact]
    public void DotnetTarget_ProjectsOnlyBelowTheRoot_LeavesDotnetToFindThem() =>
        Assert.Null(Toolchains.DotnetTarget(["src/A/A.csproj", "src/B/B.csproj"]));

    [Fact]
    public void Verdict_RestoreFailed_IsRestoreFailed() =>
        Assert.Equal(TestOutcome.RestoreFailed, Toolchains.Verdict(Toolchain.Dotnet, [Ran(Stage.Restore, 1)], null));

    [Fact]
    public void Verdict_BuildFailed_IsBuildFailed() =>
        Assert.Equal(TestOutcome.BuildFailed, Toolchains.Verdict(Toolchain.Dotnet, [Ran(Stage.Restore, 0), Ran(Stage.Build, 1)], null));

    [Fact]
    public void Verdict_StageKilledAtTheLimit_IsTimedOut() =>
        Assert.Equal(TestOutcome.TimedOut, Toolchains.Verdict(Toolchain.Dotnet, [Ran(Stage.Restore, 0), Ran(Stage.Build, null)], null));

    [Fact]
    public void Verdict_ReportCountsAFailure_IsFailed() =>
        Assert.Equal(TestOutcome.Failed, Toolchains.Verdict(Toolchain.Dotnet, Tested(1), Counts(total: 3, failed: 1)));

    /// <summary>A test host that crashed writes no failure, and its non-zero exit is still not a pass.</summary>
    [Fact]
    public void Verdict_TestsExitNonZeroWithoutAFailureCounted_IsFailed() =>
        Assert.Equal(TestOutcome.Failed, Toolchains.Verdict(Toolchain.Dotnet, Tested(1), Counts(total: 3, failed: 0)));

    [Fact]
    public void Verdict_EveryTestPassed_IsPassed() =>
        Assert.Equal(TestOutcome.Passed, Toolchains.Verdict(Toolchain.Dotnet, Tested(0), Counts(total: 3, failed: 0)));

    /// <summary>dotnet test on a solution without a test project exits 0 and writes no report.</summary>
    [Fact]
    public void Verdict_CleanExitWithoutAReport_IsNoTests() =>
        Assert.Equal(TestOutcome.NoTests, Toolchains.Verdict(Toolchain.Dotnet, Tested(0), null));

    [Fact]
    public void Verdict_ReportWithNoTest_IsNoTests() =>
        Assert.Equal(TestOutcome.NoTests, Toolchains.Verdict(Toolchain.Dotnet, Tested(0), Counts(total: 0, failed: 0)));

    /// <summary>Microsoft.Testing.Platform says 8 when no test ran — a project without tests, not a failed run.</summary>
    [Fact]
    public void Verdict_TestingPlatformRanNoTest_IsNoTests() =>
        Assert.Equal(TestOutcome.NoTests, Toolchains.Verdict(Toolchain.Dotnet, Tested(Toolchains.NoTestsRan), null));

    /// <summary>The same code with a report that counted tests is not «no tests»: the report is the word.</summary>
    [Fact]
    public void Verdict_TestingPlatformCodeWithCountedTests_IsFailed() =>
        Assert.Equal(TestOutcome.Failed, Toolchains.Verdict(Toolchain.Dotnet, Tested(Toolchains.NoTestsRan), Counts(total: 2, failed: 0)));

    [Fact]
    public void Verdict_PytestCollectedNothing_IsNoTests() =>
        Assert.Equal(TestOutcome.NoTests, Toolchains.Verdict(Toolchain.Python, [Ran(Stage.Restore, 0), Ran(Stage.Test, Toolchains.NoTestsCollected)], null));

    /// <summary>pytest 2 to 4: a collection error, an internal error, a usage error — the tests never ran.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Verdict_PytestNeverGotToTheTests_IsBuildFailed(int exitCode) =>
        Assert.Equal(TestOutcome.BuildFailed, Toolchains.Verdict(Toolchain.Python, [Ran(Stage.Restore, 0), Ran(Stage.Test, exitCode)], null));

    [Fact]
    public void Verdict_PackageWithoutATestScript_IsNoTests() =>
        Assert.Equal(TestOutcome.NoTests, Toolchains.Verdict(Toolchain.Node, Tested(Toolchains.NoTestScript), null));

    /// <summary>Node's runners write no report this sandbox reads, so a clean exit is all there is to go on.</summary>
    [Fact]
    public void Verdict_NodeTestsExitCleanWithoutAReport_IsPassed() =>
        Assert.Equal(TestOutcome.Passed, Toolchains.Verdict(Toolchain.Node, Tested(0), null));

    [Fact]
    public void Verdict_NodeTestsExitNonZero_IsFailed() =>
        Assert.Equal(TestOutcome.Failed, Toolchains.Verdict(Toolchain.Node, Tested(1), null));

    /// <summary>A solution that lists no project builds and tests nothing with exit code 0; that is a build that failed, not a project without tests.</summary>
    [Fact]
    public void Verdict_SolutionListingNoProject_IsBuildFailedAndTheRestoreSaysWhy()
    {
        var restore = new StageRun(Stage.Restore, 0, $"warning : {Toolchains.EmptySolution}! [/sandbox/project/Shop.sln]", false, TimeSpan.FromSeconds(1));
        IReadOnlyList<StageRun> stages = [restore, Ran(Stage.Build, 0), Ran(Stage.Test, 0)];

        Assert.Equal(TestOutcome.BuildFailed, Toolchains.Verdict(Toolchain.Dotnet, stages, null));
        Assert.Same(restore, Toolchains.Deciding(stages));
    }

    [Fact]
    public void Deciding_OrdinaryRun_IsTheLastStage()
    {
        IReadOnlyList<StageRun> stages = [Ran(Stage.Restore, 0), Ran(Stage.Build, 1)];

        Assert.Same(stages[^1], Toolchains.Deciding(stages));
    }

    private static StageRun Ran(Stage stage, int? exitCode) => new(stage, exitCode, string.Empty, false, TimeSpan.FromSeconds(1));

    private static IReadOnlyList<StageRun> Tested(int exitCode) => [Ran(Stage.Restore, 0), Ran(Stage.Build, 0), Ran(Stage.Test, exitCode)];

    private static TestCounts Counts(int total, int failed) => new(total, total - failed, failed, 0, []);
}
