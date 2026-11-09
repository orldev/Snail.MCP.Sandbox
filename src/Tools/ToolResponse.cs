using System.Buffers.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
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
/// Every answer is Snail.Sdk.Mcp's envelope. A run and a test run are jobs whose work ends in an <see cref="Answer{T}"/>:
/// a success ends the job succeeded with <see cref="RunSummary"/> or <see cref="TestSummary"/> as its result, a run that
/// could not happen ends it failed with the answer's error, hint and type, and the tool answers from the report — the
/// failure as the failure envelope with the report as its data, a job still running as the report with a note beside
/// it (<see cref="JobAnswer.Result"/>).
/// </para>
/// <para>
/// A test run's answer is its result. A run's is told from the run itself: its result carries every file whole, base64,
/// because a client that reads the job later has nothing but the report, and reading that back to answer the call that
/// started it — parsed, the base64 decoded and encoded again — allocated 739 MB for a file of 32 MB (measured
/// 30.10.2026). The work hands its outcome over as it ends, so a run that ended within the wait is told from the files'
/// own bytes, once, with the report beside it without the result the answer already is.
/// </para>
/// </remarks>
public static class ToolResponse
{
    /// <summary>The answers' generated JSON, with nothing escaped that the model would rather read as it is.</summary>
    private static readonly SandboxJson Json = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    /// <summary>The type a run job's result is written as.</summary>
    public static JsonTypeInfo<RunSummary> RunResult => Json.RunSummary;

    /// <summary>The type a test job's result is written as.</summary>
    public static JsonTypeInfo<TestSummary> TestResult => Json.TestSummary;

    /// <summary>The tool result of a diagnosis: the node's readiness, or why it was not diagnosed.</summary>
    public static CallToolResult From(Answer<Readiness> answer) => ToolAnswer.From(answer, AnswerFields.Of);

    /// <summary>The tool result of a run's job as it stands, told from the run itself when the job ended in time.</summary>
    /// <param name="report">The job as it stands after the wait.</param>
    /// <param name="answered">The outcome the work handed over as it ended, or null when it has not ended yet.</param>
    public static CallToolResult Run(JobReport report, Answer<RunOutcome>? answered = null)
    {
        if (report.State is JobState.Succeeded && answered is not null && answered.TryGet(out var outcome))
            return JobAnswer.Of(Stripped(report), _ => Told(outcome));

        return JobAnswer.Result(report, Running(), options: Words);
    }

    /// <summary>The tool result of a test run's job as it stands: the test run once it ended, where to read it while it runs.</summary>
    public static CallToolResult Tests(JobReport report) => JobAnswer.Result(report, Running(), options: Words);

    /// <summary>The report without its result, for the structured content of a call that carries the result as its answer.</summary>
    /// <remarks>The report beside an answer told in time would carry every file a second time, base64 inside the result: a 32 MB file twice on the wire. The resource still ends with the result whole.</remarks>
    private static JobReport Stripped(JobReport report) => report with { Result = null };

    /// <summary>What goes beside a running job's report: what to do with it.</summary>
    private static JsonObject Running() => new() { ["note"] = Messages.Running };

    /// <summary>The sandbox's words for a job that ended badly, the hint chosen for its report as the answer is written.</summary>
    private static readonly JobAnswerOptions Words = new() { Hint = HintOf };

    /// <summary>What to do about a job that ended with no hint of its work's: one the server stopped under, or one whose work threw.</summary>
    /// <remarks>A job ends failed, of the type <c>internal</c>, on any exception of its work — a store that refused a download, a daemon gone — and that is the sandbox's own fault, worded as such; only a job the host stopped under is a cancellation. A run refused with a hint of its own keeps it.</remarks>
    private static string HintOf(JobReport report) =>
        report.State is JobState.Cancelled ? Messages.NotFinished : Messages.SandboxFailed;

    /// <summary>The run told from its own outcome: the files listed in the text and carried as blocks from their bytes.</summary>
    private static CallToolResult Told(RunOutcome outcome)
    {
        var answer = ToolAnswer.Ok(AnswerFields.Of(outcome, withContent: false), Json.RunSummary, AnswerShape.None);

        foreach (var file in outcome.Outputs)
            answer.Content.Add(Block(file));

        return answer;
    }

    /// <summary>The file's block from its bytes, encoded once into the base64 the block carries, with no string in between.</summary>
    /// <remarks>Its place under /out is a URI with every segment escaped, as the platform takes it aside.</remarks>
    private static EmbeddedResourceBlock Block(OutputFile file)
    {
        var encoded = new byte[Base64.GetMaxEncodedToUtf8Length(file.Bytes.Length)];
        Base64.EncodeToUtf8(file.Bytes, encoded, out _, out _);

        return new EmbeddedResourceBlock { Resource = new BlobResourceContents { Uri = Address(file.Name), MimeType = file.MediaType, Blob = encoded } };
    }

    private static string Address(string name) =>
        $"sandbox://out/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}";
}
