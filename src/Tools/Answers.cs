using System.Text.Json.Serialization;

namespace Snail.MCP.Sandbox.Tools;

/// <summary>What one run did: the data of sandbox_run_python's answer, and its job's result.</summary>
/// <remarks>
/// The job's result is this record itself, not an envelope around it: a run that could not happen ends its job failed with
/// the answer's error, hint and type (Snail.Sdk.Mcp's <c>Jobs.Start</c> of an <c>Answer</c>), so a result is always a run.
/// </remarks>
public sealed record RunSummary(
    int? ExitCode,
    [property: JsonPropertyName("timedOut")] bool HasTimedOut,
    double Seconds,
    string Stdout,
    string Stderr,
    [property: JsonPropertyName("stdoutCut")] bool IsStdoutCut,
    [property: JsonPropertyName("stderrCut")] bool IsStderrCut,
    IReadOnlyList<OutFile> Files,
    IReadOnlyList<string> Omitted,
    string? Note);

/// <summary>A file the run left in /out: named in the text the model reads, carried whole — base64 — in the job's result.</summary>
public sealed record OutFile(
    string Name,
    string MediaType,
    int Bytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content);

/// <summary>What a test run found out: the data of sandbox_run_tests' answer, and its job's result.</summary>
public sealed record TestSummary(
    string? Toolchain,
    string Outcome,
    int? Total,
    int? Passed,
    int? Failed,
    int? Skipped,
    IReadOnlyList<FailedTest> Failures,
    double Seconds,
    string Output,
    [property: JsonPropertyName("outputCut")] bool IsOutputCut,
    IReadOnlyList<string> Omitted,
    IReadOnlyList<string>? Network,
    string? Note);

/// <summary>One test that failed, by name and message.</summary>
public sealed record FailedTest(string Name, string Message);

/// <summary>The generated JSON of the data the tools answer and their jobs end with: a native build looks nothing up at run time.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunSummary))]
[JsonSerializable(typeof(TestSummary))]
public sealed partial class SandboxJson : JsonSerializerContext;
