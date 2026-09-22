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
    public const string Libraries = "pandas, numpy, matplotlib, duckdb and openpyxl";

    public const string RunPython =
        $"""
        Run a Python 3.12 program in a fresh container and get back what it printed and the files it wrote.
        The container has no network, keeps nothing between calls and sees only the files you pass: they are in
        the working directory under their own names, read-only, so open("sales.csv") reads one of them.
        Write charts, tables and reports to /out: up to 10 files, 32 MB in all, come back with the answer.
        {Libraries} are installed; nothing can be installed during a run.
        A run gets 512 MB of memory and 1 CPU. Standard output and standard error past 64 KB each keep their
        beginning and their end with the middle cut, so print summaries rather than whole tables.
        """;

    public const string Diagnose =
        """
        Whether this sandbox can run code at all: the node's Docker daemon answers, it has the isolating runtime,
        and the executor image is there. Nothing is run. Call it when sandbox_run_python fails before the program
        starts; a program that ran and exited with an error is a fault in the program, not in the sandbox.
        """;

    public const string RunPythonCode =
        "The whole program. Nothing from an earlier call exists: import, load and compute again every time.";

    public const string RunPythonFiles =
        """
        Files the program reads: the keys of this task's files exactly as list_objects shows them, at most 20,
        each up to 64 MB and 256 MB in all. Each one appears in the working directory under its file name.
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
