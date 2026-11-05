namespace Snail.MCP.Sandbox.Application;

/// <summary>What a run has to say: its outcome, or the reason there is none.</summary>
/// <remarks>
/// Failure travels as data to the tool boundary, so a link to a forbidden host, a runtime the daemon does not
/// know and a file past its limit all reach the model as something it can act on, not as a stack trace.
/// </remarks>
public sealed record Answer<T>(T? Value, string? Error, string? Hint)
{
    /// <summary>Whether there is an outcome: no error was given.</summary>
    public bool IsSuccess => Error is null;
}

/// <summary>Builds answers without restating their type at every call.</summary>
public static class Answer
{
    /// <summary>An answer carrying its outcome.</summary>
    public static Answer<T> Ok<T>(T value) => new(value, null, null);

    /// <summary>An answer carrying why there is no outcome, and what the caller can do about it.</summary>
    public static Answer<T> Failed<T>(string error, string? hint = null) => new(default, error, hint);
}
