namespace Snail.MCP.Sandbox.Tools;

/// <summary>What the model reads before calling: the tool contract, guarded by the tests in <c>ToolCatalogTests</c>.</summary>
/// <remarks>
/// Every number here is a default of <c>SandboxOptions</c>, and a guard test fails when the two drift apart. A
/// deployment that changes a limit makes this text wrong for that deployment, so limits are changed by changing
/// the defaults, not by configuration alone.
/// </remarks>
public static class ToolDescriptions
{
    /// <summary>The libraries the executor image installs by name; a guard test compares them with its requirements.</summary>
    public const string Libraries = "pandas, numpy, matplotlib, duckdb, openpyxl and pytest";

    public const string RunPython =
        $"""
        Run a Python 3.12 program in a fresh container and get back what it printed and the files it wrote.
        The container has no network, keeps nothing between calls and sees only the files you pass: they are in
        the working directory under their own names, read-only, so open("sales.csv") reads one of them.
        Write charts, tables and reports to /out: up to 10 files, 32 MB in all, come back with the answer.
        {Libraries} are installed; nothing can be installed during a run.
        A run gets 512 MB of memory and 1 CPU. Standard output and standard error past 64 KB each keep their
        beginning and their end with the middle cut, so print summaries rather than whole tables.
        The run is a job: it answers within wait with the result, or with the job to read as a resource.
        """;

    /// <summary>The outcomes a test run answers with, as the answer spells them; a guard test compares them with <c>TestOutcome</c>.</summary>
    public const string Outcomes = "passed, failed, noTests, buildFailed, restoreFailed, timedOut or nothingToBuild";

    public const string RunTests =
        $"""
        Build a project and run its tests in a fresh container, and get back how many passed and which failed.
        The toolchain is found from the files: a .slnx, .sln, .csproj or .fsproj is built with .NET 10, a package.json
        at the root with Node 22, Python files with Python 3.12 and pytest. Packages are restored first; the build and
        the tests run with no network. A run gets 2048 MB of memory and 2 CPU.
        A project may unpack to 256 MB and 20000 files. The outcome is one of {Outcomes}.
        The answer counts the tests, names the first 20 failures with their messages, and carries the end of the
        output of the stage that decided the outcome.
        The run is a job: it answers within wait with the result, or with the job to read as a resource.
        """;

    public const string RunTestsProject =
        """
        The project as a tar or tar.gz of its working folder, without build outputs and dependencies: the key of that
        archive exactly as list_objects shows it. Nothing else of the task is visible to the run.
        """;

    public const string RunTestsToolchain =
        "dotnet, node or python; omitted, it is found from the project's files. Name it when a project mixes them.";

    public const string RunTestsTimeout =
        "Seconds the whole run may take, restore and build included, from 1 to 1800; 600 when omitted. Past it the run is killed.";

    public const string Diagnose =
        """
        Whether this sandbox can run code at all: the node's Docker daemon answers, it has the isolating runtime,
        and the executor image is there. Nothing is run. Call it when sandbox_run_python fails before the program
        starts; a program that ran and exited with an error is a fault in the program, not in the sandbox.
        untestable lists the toolchains whose image the node lacks: sandbox_run_tests cannot test those projects
        until the image is pulled onto the node, and calling again will not change that.
        """;

    public const string RunPythonCode =
        "The whole program. Nothing from an earlier call exists: import, load and compute again every time.";

    public const string RunPythonFiles =
        """
        Files the program reads: the keys of this task's files exactly as list_objects shows them, at most 20,
        each up to 64 MB and 256 MB in all. Each one appears in the working directory under its file name.
        """;

    /// <summary>The longest wait a call may ask for: the longest test run and the grace its container outlives it by; a guard test holds it to the defaults.</summary>
    public const int LongestWait = 1860;

    public const string Wait =
        """
        Seconds to wait for the run before answering with the job in flight, from 0 to 1860; 60 when
        omitted, 0 answers at once. A run that ends within it answers as described. One that does not answers with
        ok true and job, the URI of a resource whose text is the job's report; the report ends with the same answer
        as its result, every file in it base64. A client that can wait awaits that resource; it is not asked again.
        """;

    public const string RunPythonTimeout =
        "Seconds the run may take, from 1 to 300; 60 when omitted. Past it the run is killed and nothing from /out comes back.";

    public const string FindTool =
        """
        Which tool does a thing, said in your own words — "draw a chart from this table", "check the node
        before I try again". Answers with the tools that match: the name to call and every argument each of
        them takes, with what it means. When nothing matches it answers with all of them, because this
        server has few enough tools that the whole list is a better answer than a wrong one.
        """;

    public const string FindToolTask = "What you are trying to do, in the words you would use to describe it.";

    public const string FindToolLimit = "How many tools to return at most; 5 by default, and never more than this server has.";
}
