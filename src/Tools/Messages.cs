namespace Snail.MCP.Sandbox.Tools;

/// <summary>The notes a tool result carries about how a run ended, for the model to act on.</summary>
public static class Messages
{
    /// <summary>The note of a run killed at its time limit.</summary>
    public const string TimedOut =
        "The run was killed at its time limit, so nothing from /out came back. Do less in one call, or ask for a longer timeoutSeconds.";

    /// <summary>The note of a run that exited 137, which is the kernel killing it.</summary>
    public const string Killed =
        "Exit code 137: the run was killed, almost always for passing its memory limit. Read large files in parts, or keep fewer columns.";

    /// <summary>The note of a program that ran and failed.</summary>
    public const string Failed =
        "The program failed; the end of stderr says why. Fix that and run the whole program again.";

    /// <summary>The note of a run some of whose files did not come back.</summary>
    public const string Omitted =
        "Some files in /out did not come back; omitted says which and why.";

    /// <summary>The note of a test run in which tests failed.</summary>
    public const string TestsFailed =
        "Tests failed; failures names them. Fix the code, or the test when the test is what is wrong, and run the tests again.";

    /// <summary>The note of a project that did not build.</summary>
    public const string BuildFailed =
        "The project did not build, so no test ran; the end of output says why. Fix that and run the tests again.";

    /// <summary>The note of a project whose packages were not restored.</summary>
    public const string RestoreFailed =
        "The packages were not restored, so nothing was built. A package name or version may be wrong, or the package is not in the sandbox's cache and restore has no network here.";

    /// <summary>The note of a test run killed at its time limit.</summary>
    public const string TestsTimedOut =
        "The run was killed at its time limit. Ask for a longer timeoutSeconds, or run a smaller part of the tests.";

    /// <summary>The note of a project that built and has no tests.</summary>
    public const string NoTests =
        "The project built and has no tests.";

    /// <summary>The note of an archive holding nothing this sandbox builds.</summary>
    public const string NothingToBuild =
        "Nothing in the archive is a .NET, Node or Python project, so nothing was built.";

    /// <summary>The note of a project some of whose files were not copied in.</summary>
    public const string ProjectOmitted =
        "Some files of the project were not copied in; omitted says which and why.";

    /// <summary>The note of a run still going when the wait ended.</summary>
    public const string Running =
        "The run is still going. Read the job resource when you want its state; it ends with the run as its result, any files in it base64. A client that can wait awaits the resource instead of asking again.";

    /// <summary>The hint of a job the server stopped under.</summary>
    public const string NotFinished =
        "The sandbox stopped before the run ended — it was shutting down, or the run was cancelled. Start the run again.";

    /// <summary>The hint of a wait outside what a call may ask for.</summary>
    public const string WaitOutsideTheLimit =
        "Ask with a wait within the range, or 0 to get the job at once and read its resource later.";

    /// <summary>The hint of a run the sandbox itself could not carry out.</summary>
    public const string SandboxFailed =
        "This is the sandbox failing, not the code. Call once more; if it fails again, say so in the answer.";
}
