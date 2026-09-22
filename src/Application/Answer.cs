namespace Snail.MCP.Sandbox.Application;

/// <summary>What a run has to say: its outcome, or the reason there is none.</summary>
/// <remarks>
/// Failure travels as data to the tool boundary, so a link to a forbidden host, a runtime the daemon does not
/// know and a file past its limit all reach the model as something it can act on, not as a stack trace.
/// </remarks>
public sealed record Answer<T>(T? Value, string? Error, string? Hint)
{
    public bool IsSuccess => Error is null;
}

/// <summary>Builds answers without restating their type at every call.</summary>
public static class Answer
{
    public static Answer<T> Ok<T>(T value) => new(value, null, null);

    public static Answer<T> Failed<T>(string error, string? hint = null) => new(default, error, hint);
}
