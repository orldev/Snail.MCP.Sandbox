namespace Snail.MCP.Sandbox.Tools;

/// <summary>What the model is told about this server when it connects.</summary>
/// <remarks>
/// Read once, before any call, so it carries what a model cannot find out by trying without wasting a step:
/// that every call starts from nothing, and that files go in by key and come out through /out.
/// </remarks>
public static class ServerGuidance
{
    /// <summary>The instructions a client receives in initialize, before any call.</summary>
    public const string Instructions =
        """
        This server runs Python you write in a fresh, isolated container: no network, no state kept between
        calls, and only the files you pass in. Use it for work SQL cannot express well — charts, fitting,
        reshaping data across several files.

        sandbox_run_python takes the whole program every time: import, load the files and compute again, since
        nothing from an earlier call exists. Pass the task's files by key; they appear in the working directory
        under their names. Write what should come back to /out. Print short summaries: long output is cut.

        sandbox_run_tests builds a whole project and runs its tests: pass the key of the project's archive. It answers
        with an outcome and the counts; failures name the tests that failed and why. Fix what they name and run again.

        Both are jobs: a call answers within its wait with the result, or with the job's resource when the run is
        still going — a client that can wait awaits that resource, and finds the same answer in its result.

        When a call fails before the program starts, sandbox_diagnose says whether the node can run code at all.
        find_tool names the tool for a thing said in your own words, and the arguments it takes.

        Every answer is JSON with an ok flag. When ok is false the program did not run at all: read error and hint.
        When ok is true, a non-zero exitCode means your program failed, and stderr ends with the reason.
        """;
}
