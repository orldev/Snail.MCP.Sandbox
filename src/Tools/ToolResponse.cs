using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Snail.MCP.Sandbox.Application;

namespace Snail.MCP.Sandbox.Tools;

/// <summary>A run as a tool result: what it printed as text, and every file it left as a content block of its own.</summary>
/// <remarks>
/// Files travel as blocks rather than inside the JSON, because the platform takes blocks carrying a media type
/// and a base64 body aside into its object store and leaves the model a line naming each. Every file, pictures
/// included, goes as an embedded resource: an image block has nowhere to carry a name, and a chart stored as
/// a nameless PNG is one nobody can find on the task's page or pass to the next run by what it is called.
/// A run that ran counts as a success even when the program failed: the failure is the program's, told in its
/// exit code and stderr. Only a run that could not happen is flagged as an error.
/// </remarks>
public static class ToolResponse
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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

    public static CallToolResult From(Answer<RunOutcome> answer) =>
        answer.Value is { } outcome ? Success(outcome) : Failure(answer.Error ?? "The run did not happen.", answer.Hint);

    public static CallToolResult From(Answer<TestReport> answer) =>
        answer.Value is { } report ? Success(Data(report)) : Failure(answer.Error ?? "The tests did not run.", answer.Hint);

    public static CallToolResult Success(JsonObject data) => new()
    {
        Content = [new TextContentBlock { Text = new JsonObject { ["ok"] = true, ["data"] = data.DeepClone() }.ToJsonString(Options) }],
        StructuredContent = JsonSerializer.SerializeToElement(data, Options)
    };

    public static CallToolResult Success(RunOutcome outcome)
    {
        var data = Data(outcome);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = new JsonObject { ["ok"] = true, ["data"] = data.DeepClone() }.ToJsonString(Options) }, .. outcome.Outputs.Select(Block)],
            StructuredContent = JsonSerializer.SerializeToElement(data, Options)
        };
    }

    public static CallToolResult Failure(string message, string? hint = null) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = new JsonObject { ["ok"] = false, ["error"] = message, ["hint"] = hint }.ToJsonString(Options) }],
        StructuredContent = JsonSerializer.SerializeToElement(new JsonObject { ["error"] = message, ["hint"] = hint }, Options)
    };

    private static JsonObject Data(RunOutcome outcome) => new()
    {
        ["exitCode"] = outcome.ExitCode,
        ["timedOut"] = outcome.HasTimedOut,
        ["seconds"] = Math.Round(outcome.Duration.TotalSeconds, 2),
        ["stdout"] = outcome.Stdout,
        ["stderr"] = outcome.Stderr,
        ["stdoutCut"] = outcome.IsStdoutCut,
        ["stderrCut"] = outcome.IsStderrCut,
        ["files"] = new JsonArray([.. outcome.Outputs.Select(file => new JsonObject { ["name"] = file.Name, ["mediaType"] = file.MediaType, ["bytes"] = file.Bytes.Length })]),
        ["omitted"] = new JsonArray([.. outcome.Omitted.Select(reason => JsonValue.Create(reason))]),
        ["note"] = Note(outcome)
    };

    private static JsonObject Data(TestReport report) => new()
    {
        ["toolchain"] = report.Toolchain is { } toolchain ? JsonNamingPolicy.CamelCase.ConvertName(toolchain.ToString()) : null,
        ["outcome"] = JsonNamingPolicy.CamelCase.ConvertName(report.Outcome.ToString()),
        ["total"] = report.Counts?.Total,
        ["passed"] = report.Counts?.Passed,
        ["failed"] = report.Counts?.Failed,
        ["skipped"] = report.Counts?.Skipped,
        ["failures"] = new JsonArray([.. (report.Counts?.Failures ?? []).Select(failure => new JsonObject { ["name"] = failure.Name, ["message"] = failure.Message })]),
        ["seconds"] = Math.Round(report.Duration.TotalSeconds, 2),
        ["output"] = report.Output,
        ["outputCut"] = report.IsOutputCut,
        ["omitted"] = new JsonArray([.. report.Omitted.Select(reason => JsonValue.Create(reason))]),
        ["network"] = report.Contacted is null ? null : new JsonArray([.. report.Contacted.Select(host => JsonValue.Create(host))]),
        ["note"] = Note(report)
    };

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

    private static EmbeddedResourceBlock Block(OutputFile file) =>
        new() { Resource = BlobResourceContents.FromBytes(file.Bytes, Address(file.Name), file.MediaType) };

    /// <summary>The file's place under /out as a URI, each segment escaped, so a name with spaces still reads back.</summary>
    private static string Address(string name) =>
        $"sandbox://out/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}";
}
