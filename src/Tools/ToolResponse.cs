using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using Snail.MCP.Sandbox.Application;
using Snail.Sdk.Mcp.Hosting.Jobs;

namespace Snail.MCP.Sandbox.Tools;

/// <summary>A run as a tool result: what it printed as text, and every file it left as a content block of its own.</summary>
/// <remarks>
/// <para>
/// Files travel as blocks rather than inside the JSON, because the platform takes blocks carrying a media type
/// and a base64 body aside into its object store and leaves the model a line naming each. Every file, pictures
/// included, goes as an embedded resource: an image block has nowhere to carry a name, and a chart stored as
/// a nameless PNG is one nobody can find on the task's page or pass to the next run by what it is called.
/// A run that ran counts as a success even when the program failed: the failure is the program's, told in its
/// exit code and stderr. Only a run that could not happen is flagged as an error.
/// </para>
/// <para>
/// A run is a job: the tool answers from the job's report. Ended within the wait, the report's result is the answer
/// above, read back and told with its files as blocks; still running, the answer is where to read the job. The result
/// carries the files whole, base64, because a client that reads the job later has nothing but the report.
/// </para>
/// </remarks>
public static class ToolResponse
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The answers' generated JSON, with nothing escaped that the model would rather read as it is.</summary>
    private static readonly SandboxJson Json = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    /// <summary>The type a run job's result is written as.</summary>
    public static JsonTypeInfo<RunAnswer> RunResult => Json.RunAnswer;

    /// <summary>The type a test job's result is written as.</summary>
    public static JsonTypeInfo<TestAnswer> TestResult => Json.TestAnswer;

    public static CallToolResult From(Answer<Readiness> answer) =>
        answer.Value is { } readiness
            ? Success(new JsonObject
            {
                ["dockerVersion"] = readiness.DockerVersion,
                ["runtime"] = readiness.Runtime,
                ["image"] = readiness.Image,
                ["untestable"] = new JsonArray([.. readiness.Untestable.Select(toolchain => (JsonNode)JsonNamingPolicy.CamelCase.ConvertName(toolchain.ToString()))])
            })
            : Failure(answer.Error ?? "The sandbox was not diagnosed.", answer.Hint);

    /// <summary>What a run's job ends with: the run, or why there was none.</summary>
    public static RunAnswer Answered(Answer<RunOutcome> answer) =>
        answer.Value is { } outcome
            ? new RunAnswer(true, Data(outcome), null, null)
            : new RunAnswer(false, null, answer.Error ?? "The run did not happen.", answer.Hint);

    /// <summary>What a test run's job ends with: the report, or why there was none.</summary>
    public static TestAnswer Answered(Answer<TestReport> answer) =>
        answer.Value is { } report
            ? new TestAnswer(true, Data(report), null, null)
            : new TestAnswer(false, null, answer.Error ?? "The tests did not run.", answer.Hint);

    /// <summary>The tool result of a run's job as it stands: the answer with its files as blocks once it ended, where to read it while it runs.</summary>
    public static CallToolResult Run(JobReport report) => JobAnswer.Of(report, ended => Said(ended, Json.RunAnswer, Told));

    /// <summary>The tool result of a test run's job as it stands.</summary>
    public static CallToolResult Tests(JobReport report) => JobAnswer.Of(report, ended => Said(ended, Json.TestAnswer, Told));

    public static CallToolResult Success(JsonObject data) => new()
    {
        Content = [new TextContentBlock { Text = new JsonObject { ["ok"] = true, ["data"] = data.DeepClone() }.ToJsonString(Options) }],
        StructuredContent = Element(data)
    };

    public static CallToolResult Failure(string message, string? hint = null) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new FailedAnswer(false, message, hint), Json.FailedAnswer) }],
        StructuredContent = Element(new JsonObject { ["error"] = message, ["hint"] = hint })
    };

    private static CallToolResult Said<T>(JobReport report, JsonTypeInfo<T> type, Func<T, CallToolResult> told)
    {
        if (report.IsRunning)
            return Pending(report);

        if (report.State is not JobState.Succeeded || report.Result is not { } result)
            return Failure($"The sandbox did not finish the run: {report.Message ?? "it was stopped"}.", Messages.NotFinished);

        return told(JsonSerializer.Deserialize(result, type)!);
    }

    private static CallToolResult Pending(JobReport report) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new PendingAnswer(true, report.Uri, "running", report.Progress, Messages.Running), Json.PendingAnswer) }]
    };

    private static CallToolResult Told(RunAnswer answer)
    {
        if (answer.Data is not { } data)
            return Failure(answer.Error ?? "The run did not happen.", answer.Hint);

        var listed = answer with { Data = data with { Files = [.. data.Files.Select(file => file with { Content = null })] } };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(listed, Json.RunAnswer) }, .. data.Files.Select(Block)]
        };
    }

    private static CallToolResult Told(TestAnswer answer) =>
        answer.Data is null
            ? Failure(answer.Error ?? "The tests did not run.", answer.Hint)
            : new CallToolResult { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(answer, Json.TestAnswer) }] };

    private static RunSummary Data(RunOutcome outcome) => new(
        outcome.ExitCode,
        outcome.HasTimedOut,
        Math.Round(outcome.Duration.TotalSeconds, 2),
        outcome.Stdout,
        outcome.Stderr,
        outcome.IsStdoutCut,
        outcome.IsStderrCut,
        [.. outcome.Outputs.Select(file => new OutFile(file.Name, file.MediaType, file.Bytes.Length, Convert.ToBase64String(file.Bytes)))],
        outcome.Omitted,
        Note(outcome));

    private static TestSummary Data(TestReport report) => new(
        report.Toolchain is { } toolchain ? JsonNamingPolicy.CamelCase.ConvertName(toolchain.ToString()) : null,
        JsonNamingPolicy.CamelCase.ConvertName(report.Outcome.ToString()),
        report.Counts?.Total,
        report.Counts?.Passed,
        report.Counts?.Failed,
        report.Counts?.Skipped,
        [.. (report.Counts?.Failures ?? []).Select(failure => new FailedTest(failure.Name, failure.Message))],
        Math.Round(report.Duration.TotalSeconds, 2),
        report.Output,
        report.IsOutputCut,
        report.Omitted,
        report.Contacted,
        Note(report));

    private static string? Note(TestReport report) => report.Outcome switch
    {
        TestOutcome.Failed => Messages.TestsFailed,
        TestOutcome.BuildFailed => Messages.BuildFailed,
        TestOutcome.RestoreFailed => Messages.RestoreFailed,
        TestOutcome.TimedOut => Messages.TestsTimedOut,
        TestOutcome.NoTests => Messages.NoTests,
        TestOutcome.NothingToBuild => Messages.NothingToBuild,
        _ when report.Omitted.Count > 0 => Messages.ProjectOmitted,
        _ => null
    };

    private static string? Note(RunOutcome outcome) => outcome switch
    {
        { HasTimedOut: true } => Messages.TimedOut,
        { ExitCode: 137 } => Messages.Killed,
        { ExitCode: not 0 } => Messages.Failed,
        { Omitted.Count: > 0 } => Messages.Omitted,
        _ => null
    };

    /// <summary>The file as the platform takes it aside: its base64 body, its media type, and its place under /out as a URI with every segment escaped.</summary>
    private static EmbeddedResourceBlock Block(OutFile file) =>
        new() { Resource = BlobResourceContents.FromBytes(Convert.FromBase64String(file.Content ?? string.Empty), Address(file.Name), file.MediaType) };

    private static string Address(string name) =>
        $"sandbox://out/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}";

    /// <summary>The node as an element, written and read back rather than serialized: a native build looks no type up for it.</summary>
    private static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString(Options));

        return document.RootElement.Clone();
    }
}
