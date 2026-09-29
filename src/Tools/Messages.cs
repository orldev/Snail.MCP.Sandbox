namespace Snail.MCP.Sandbox.Tools;

/// <summary>The notes a tool result carries about how a run ended, for the model to act on.</summary>
public static class Messages
{
    public const string TimedOut =
        "The run was killed at its time limit, so nothing from /out came back. Do less in one call, or ask for a longer timeoutSeconds.";

    public const string Killed =
        "Exit code 137: the run was killed, almost always for passing its memory limit. Read large files in parts, or keep fewer columns.";

    public const string Failed =
        "The program failed; the end of stderr says why. Fix that and run the whole program again.";

    public const string Omitted =
        "Some files in /out did not come back; omitted says which and why.";

    public const string TestsFailed =
        "Tests failed; failures names them. Fix the code, or the test when the test is what is wrong, and run the tests again.";

    public const string BuildFailed =
        "The project did not build, so no test ran; the end of output says why. Fix that and run the tests again.";

    public const string RestoreFailed =
        "The packages were not restored, so nothing was built. A package name or version may be wrong, or the package is not in the sandbox's cache and restore has no network here.";

    public const string TestsTimedOut =
        "The run was killed at its time limit. Ask for a longer timeoutSeconds, or run a smaller part of the tests.";

    public const string NoTests =
        "The project built and has no tests.";

    public const string NothingToBuild =
        "Nothing in the archive is a .NET, Node or Python project, so nothing was built.";

    public const string ProjectOmitted =
        "Some files of the project were not copied in; omitted says which and why.";
}
