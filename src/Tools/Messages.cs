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
}
