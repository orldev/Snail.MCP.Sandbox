namespace Snail.MCP.Sandbox.Domain;

/// <summary>Which toolchain a project needs, what it is called by a caller, and what its stages add up to.</summary>
public static class Toolchains
{
    /// <summary>The exit code the Node test stage uses to say the package declares no test script.</summary>
    /// <remarks>Chosen outside what npm, node and the shell give themselves, so it cannot be mistaken for a test run that failed.</remarks>
    public const int NoTestScript = 86;

    /// <summary>What dotnet restore says, and still exits 0, when the solution it was pointed at lists no project.</summary>
    /// <remarks>
    /// Such a solution builds nothing and tests nothing, all with exit code 0, and would be answered «no tests» — true, and
    /// no help: the project has tests that are simply not in the solution. It is a build that failed, and this line is why.
    /// </remarks>
    public const string EmptySolution = "Unable to find a project to restore";

    /// <summary>pytest's own exit code for a run that collected no test.</summary>
    public const int NoTestsCollected = 5;

    /// <summary>Microsoft.Testing.Platform's exit code for a run in which no test ran.</summary>
    public const int NoTestsRan = 8;

    /// <summary>Microsoft.Testing.Platform's exit code for a command line naming an option no extension of the project offers.</summary>
    public const int UnknownOption = 5;

    /// <summary>The names a caller may give, in the order they are tried when none is given.</summary>
    public static readonly IReadOnlyList<string> Names = ["dotnet", "node", "python"];

    /// <summary>The toolchain a project's files call for, or null when nothing in it is one this sandbox builds.</summary>
    /// <remarks>
    /// .NET before Node before Python: a solution that carries a web front-end or a few scripts is still built by
    /// dotnet, and a Node package with a Python helper by npm. Node is recognised only by a package.json at the root,
    /// because one deep inside a .NET or Python project is a tool of that project, not the project.
    /// </remarks>
    public static Toolchain? Of(IReadOnlyList<string> paths)
    {
        if (paths.Any(IsDotnetProject))
            return Toolchain.Dotnet;

        if (paths.Contains("package.json", StringComparer.Ordinal))
            return Toolchain.Node;

        return paths.Any(IsPython) ? Toolchain.Python : null;
    }

    /// <summary>The toolchain a caller named, or null for a name this sandbox does not know.</summary>
    public static Toolchain? Named(string name) => name.Trim().ToLowerInvariant() switch
    {
        "dotnet" => Toolchain.Dotnet,
        "node" => Toolchain.Node,
        "python" => Toolchain.Python,
        _ => null
    };

    /// <summary>The solution or project at the root dotnet is pointed at; null leaves dotnet to find one itself.</summary>
    /// <remarks>
    /// A root with two solutions stops dotnet with MSB1011 before it builds anything, so one is chosen: a .slnx over
    /// a .sln, since a repository keeping both has moved to the new format, then the first by name.
    /// </remarks>
    public static string? DotnetTarget(IReadOnlyList<string> paths)
    {
        var root = paths.Where(path => !path.Contains('/', StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

        return root.FirstOrDefault(path => path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            ?? root.FirstOrDefault(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            ?? root.FirstOrDefault(IsDotnetProject);
    }

    /// <summary>What the stages add up to.</summary>
    /// <remarks>
    /// Restore and build stop the run when they fail, so a failed stage is always the last. The test stage is read
    /// through the framework's own conventions: pytest says 5 for no tests and 2 to 4 for a run that never got to
    /// the tests; a failed test is counted from the report. A zero exit with no report is a project without tests —
    /// except under Node, whose runners write no report this sandbox reads, so a clean exit there is a pass.
    /// </remarks>
    public static TestOutcome Verdict(Toolchain toolchain, IReadOnlyList<StageRun> stages, TestCounts? counts)
    {
        var last = stages[^1];

        if (last.ExitCode is not { } exitCode)
            return TestOutcome.TimedOut;

        if (last.Stage is Stage.Restore && exitCode != 0)
            return TestOutcome.RestoreFailed;

        if (last.Stage is Stage.Build && exitCode != 0)
            return TestOutcome.BuildFailed;

        if (toolchain is Toolchain.Dotnet && Emptied(stages) is not null)
            return TestOutcome.BuildFailed;

        return toolchain switch
        {
            Toolchain.Python when exitCode == NoTestsCollected => TestOutcome.NoTests,
            Toolchain.Python when exitCode is >= 2 and <= 4 => TestOutcome.BuildFailed,
            Toolchain.Node when exitCode == NoTestScript => TestOutcome.NoTests,
            Toolchain.Dotnet when exitCode == NoTestsRan && counts is null or { Total: 0 } => TestOutcome.NoTests,
            _ when counts is { Failed: > 0 } || exitCode != 0 => TestOutcome.Failed,
            Toolchain.Node => TestOutcome.Passed,
            _ when counts is null or { Total: 0 } => TestOutcome.NoTests,
            _ => TestOutcome.Passed
        };
    }

    /// <summary>The stage whose output explains the outcome: the restore that found no project, when that is what happened, the last stage otherwise.</summary>
    public static StageRun Deciding(IReadOnlyList<StageRun> stages) => Emptied(stages) ?? stages[^1];

    private static StageRun? Emptied(IReadOnlyList<StageRun> stages) =>
        stages.FirstOrDefault(stage => stage.Stage is Stage.Restore && stage.Output.Contains(EmptySolution, StringComparison.Ordinal));

    private static bool IsDotnetProject(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

    private static bool IsPython(string path) =>
        path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
        || path is "pyproject.toml" or "requirements.txt" or "setup.py";
}
