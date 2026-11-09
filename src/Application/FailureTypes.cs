namespace Snail.MCP.Sandbox.Application;

/// <summary>The <c>type</c> of a refusal: the word a program branches on, where the error is prose for the model.</summary>
/// <remarks>
/// A contract like an outcome's name: the platform tells a node that is full from one that is not set up by this word,
/// never by the error's text, which stays free to reword. The SDK's words where one fits, camel case like them where none
/// does. A run that could not happen ends its job failed with this type, so a job's report carries it too; before, every
/// refusal inside a job read <c>failed</c>.
/// </remarks>
public static class FailureTypes
{
    /// <summary>A value the tool cannot work with: no code, no project, a key where a link was due, a timeout, wait or toolchain outside what it takes, too many files.</summary>
    /// <remarks>The SDK's word for an argument that did not bind, since to the caller both are the same mistake.</remarks>
    public const string Arguments = "arguments";

    /// <summary>The node is full: every place taken and as many waiting as may wait. A place frees when a run ends.</summary>
    public const string Busy = "busy";

    /// <summary>The node cannot run anything until a person sets it up: no runtime, no image, a Docker daemon that does not answer.</summary>
    public const string NotReady = "notReady";

    /// <summary>A link to a host the deployment does not name.</summary>
    public const string NotAllowed = "notAllowed";

    /// <summary>The object store did not answer, or a download broke off or outlasted its time.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The store answered the link with an error: a signed link that expired, most often.</summary>
    public const string LinkRefused = "linkRefused";

    /// <summary>A file, or a project unpacked, past the limit left.</summary>
    public const string TooLarge = "tooLarge";

    /// <summary>A project archive that is no tar, or holds an entry pointing outside the project.</summary>
    /// <remarks>The SDK's word for a root whose names could climb out of the copy it seems to name.</remarks>
    public const string Malformed = "malformed";

    /// <summary>The sandbox itself failed to carry the run out: not the code, not the call.</summary>
    /// <remarks>The SDK's word for a failure nobody foresaw, which a thrown run's job already ends with.</remarks>
    public const string Internal = "internal";
}
