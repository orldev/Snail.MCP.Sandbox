using System.Text.Json.Serialization;

namespace Snail.MCP.Sandbox.Tools;

/// <summary>What sandbox_run_python answers, and what its job's result carries: the run, or why there was none.</summary>
/// <remarks>Data, error and hint are left out when null, so the text reads as it always has: ok with data, or ok false with error and hint.</remarks>
public sealed record RunAnswer(
    [property: JsonPropertyName("ok")] bool IsOk,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunSummary? Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint);

/// <summary>What one run did, as the answer carries it.</summary>
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

/// <summary>What sandbox_run_tests answers, and what its job's result carries: the test run, or why there was none.</summary>
public sealed record TestAnswer(
    [property: JsonPropertyName("ok")] bool IsOk,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TestSummary? Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint);

/// <summary>What a test run found out, as the answer carries it.</summary>
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

/// <summary>What a tool answers while its job still runs: where to read the job, and what to do with it.</summary>
public sealed record PendingAnswer(
    [property: JsonPropertyName("ok")] bool IsOk,
    string Job,
    string State,
    double? Progress,
    string Note);

/// <summary>What a tool answers when the run could not happen, or its job did not end with a result.</summary>
public sealed record FailedAnswer(
    [property: JsonPropertyName("ok")] bool IsOk,
    string Error,
    string? Hint);

/// <summary>The generated JSON of what the tools answer and their jobs end with: a native build looks nothing up at run time.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunAnswer))]
[JsonSerializable(typeof(TestAnswer))]
[JsonSerializable(typeof(PendingAnswer))]
[JsonSerializable(typeof(FailedAnswer))]
public sealed partial class SandboxJson : JsonSerializerContext;
