namespace Snail.MCP.Sandbox.Domain;

/// <summary>One piece of code to run, the files it reads and how long it may take.</summary>
public sealed record RunRequest(string Code, IReadOnlyList<Uri> Files, TimeSpan Timeout);

/// <summary>An input downloaded for a run, where the code finds it.</summary>
public sealed record InputFile(string Name, string Path, long Bytes);

/// <summary>What the node can run code with: the daemon's version, the isolating runtime it knows, the executor image it holds.</summary>
public sealed record Readiness(string DockerVersion, string Runtime, string Image);

/// <summary>A file the code left in /out.</summary>
public sealed record OutputFile(string Name, string MediaType, byte[] Bytes);

/// <summary>What one run did: its exit, its output and the files it left.</summary>
/// <remarks>
/// Cut output and dropped files are named rather than hidden: a model shown the first screen of a long
/// traceback, and not told, reasons about an error it never saw the end of. The exit code is unknown when
/// the run was killed at its timeout, and stays unknown rather than borrowing the kill's own code.
/// </remarks>
public sealed record RunOutcome(int? ExitCode, string Stdout, string Stderr, TimeSpan Duration)
{
    public bool TimedOut { get; init; }

    public bool IsStdoutCut { get; init; }

    public bool IsStderrCut { get; init; }

    public IReadOnlyList<OutputFile> Outputs { get; init; } = [];

    /// <summary>Files left in /out that were not returned, each with the reason.</summary>
    public IReadOnlyList<string> Omitted { get; init; } = [];
}
