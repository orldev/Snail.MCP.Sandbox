namespace Snail.MCP.Sandbox.Domain;

/// <summary>The language a project is built and tested with, which decides the image and the commands.</summary>
public enum Toolchain
{
    Dotnet,
    Python,
    Node
}

/// <summary>How a test run of a project ended, from the model's side: what to do next depends on which one it is.</summary>
public enum TestOutcome
{
    /// <summary>Every test that ran passed.</summary>
    Passed,

    /// <summary>At least one test failed; the failures name them.</summary>
    Failed,

    /// <summary>The project built and has no tests.</summary>
    NoTests,

    /// <summary>The project did not build, so no test ran; the output says why.</summary>
    BuildFailed,

    /// <summary>The packages the project depends on were not restored; nothing was built.</summary>
    RestoreFailed,

    /// <summary>The run passed its time limit and was killed.</summary>
    TimedOut,

    /// <summary>The archive holds nothing this sandbox knows how to build.</summary>
    NothingToBuild
}

/// <summary>A project as it reached the node: an archive rewritten for the container, and what was left out of it.</summary>
/// <param name="Archive">The tar the container is filled from, entries owned by the run user under the project folder.</param>
/// <param name="Paths">Every regular file of the project, relative to its root.</param>
/// <param name="Omitted">Entries that were not copied, each with the reason.</param>
public sealed record PackedProject(string Archive, IReadOnlyList<string> Paths, IReadOnlyList<string> Omitted);

/// <summary>One test that failed, by the name the test framework gave it.</summary>
public sealed record TestFailure(string Name, string Message);

/// <summary>The counts and failures read from the reports a test run wrote.</summary>
public sealed record TestCounts(int Total, int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures);

/// <summary>A stage of a test run, each one a command of its own in the same container.</summary>
public enum Stage
{
    /// <summary>Fetching the packages the project depends on: the only stage with a network, and only when one is configured.</summary>
    Restore,

    /// <summary>Compiling, offline.</summary>
    Build,

    /// <summary>Running the tests, offline.</summary>
    Test
}

/// <summary>What one stage did: its exit code, null when it was killed at the time limit, and what it printed.</summary>
public sealed record StageRun(Stage Stage, int? ExitCode, string Output, bool IsOutputCut, TimeSpan Duration);

/// <summary>Everything the container reported back: the stages in the order they ran, and the counts the reports gave.</summary>
/// <param name="Stages">The stages that ran; a stage that failed is the last one.</param>
/// <param name="Counts">Read from the reports the tests wrote; null when none were written.</param>
public sealed record TestExecution(IReadOnlyList<StageRun> Stages, TestCounts? Counts)
{
    /// <summary>The hosts the restore asked the proxy for, each once; null when no proxy watched the run.</summary>
    public IReadOnlyList<string>? Contacted { get; init; }
}

/// <summary>What a test run of a project found out.</summary>
/// <remarks>
/// The counts come from the reports the test framework wrote, never from its console: a console is worded per
/// version and per language, and a model reading «243/243» must be reading a number, not a guess. The output is
/// the end of the phase that decided the outcome, because that is where a build error or a failed restore is.
/// </remarks>
public sealed record TestReport(Toolchain? Toolchain, TestOutcome Outcome, TimeSpan Duration)
{
    /// <summary>Null when no report was written: a project without tests, a build that failed, or a framework that writes none.</summary>
    public TestCounts? Counts { get; init; }

    /// <summary>What the deciding phase printed, standard error after standard output, cut in the middle past the limit.</summary>
    public string Output { get; init; } = string.Empty;

    public bool IsOutputCut { get; init; }

    /// <summary>Files of the project that did not reach the container, and why.</summary>
    public IReadOnlyList<string> Omitted { get; init; } = [];

    /// <summary>The hosts the restore reached, each once; empty when it reached none, null when nothing watched it.</summary>
    public IReadOnlyList<string>? Contacted { get; init; }
}
