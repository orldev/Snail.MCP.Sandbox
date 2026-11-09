using System.Text.Json;
using ModelContextProtocol.Protocol;
using Snail.MCP.Sandbox.Domain;
using Snail.MCP.Sandbox.Tools;
using Snail.Sdk.Mcp.Hosting.Jobs;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>A run's answer as the model reads it, told from the job's report the way the tool tells it.</summary>
public sealed class ToolResponseTests
{
    private static readonly RunOutcome Ran = new(0, "rows: 12\n", string.Empty, TimeSpan.FromSeconds(1.234));

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The program failing is still a run that happened; only a run that could not happen is a protocol error.</summary>
    [Fact]
    public async Task Run_ProgramExitsWithAnError_IsNotAToolError()
    {
        var result = await RunAsync(Answer.Ok(Ran with { ExitCode = 1, Stderr = "KeyError: 'total'" }));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(Messages.Failed, Data(result).GetProperty("note").GetString());
    }

    /// <summary>The model names a toolchain as sandbox_run_tests takes it, so an untestable one reads the same way.</summary>
    [Fact]
    public void From_NodeWithoutAToolchainImage_NamesItAsTheToolTakesIt()
    {
        var result = ToolResponse.From(Answer.Ok(new Readiness("29.1.0", "runsc", "snail/sandbox-python:latest") { Untestable = [Toolchain.Dotnet, Toolchain.Node] }));

        Assert.Equal(["dotnet", "node"], Data(result).GetProperty("untestable").EnumerateArray().Select(name => name.GetString()));
    }

    /// <summary>The platform shows where a restore went; «none watched» and «went nowhere» must read differently.</summary>
    [Fact]
    public async Task Tests_TestReport_AnswersTheHostsTheRestoreReachedOrNullWhenNobodyWatched()
    {
        var watched = await TestsAsync(Answer.Ok(new TestReport(Toolchain.Python, TestOutcome.Passed, TimeSpan.FromSeconds(3)) { Contacted = ["pypi.org", "files.pythonhosted.org"] }));
        var unwatched = await TestsAsync(Answer.Ok(new TestReport(Toolchain.Python, TestOutcome.Passed, TimeSpan.FromSeconds(3))));

        Assert.Equal(["pypi.org", "files.pythonhosted.org"], Data(watched).GetProperty("network").EnumerateArray().Select(host => host.GetString()));
        Assert.Equal(JsonValueKind.Null, Data(unwatched).GetProperty("network").ValueKind);
    }

    [Fact]
    public async Task Run_RunThatCouldNotHappen_IsAToolErrorWithItsHint()
    {
        var result = await RunAsync(Answer.Failed<RunOutcome>("The sandbox node has no runsc runtime.", "Say so in the answer."));

        Assert.True(result.IsError);
        Assert.Contains("Say so in the answer.", Assert.IsType<TextContentBlock>(result.Content[0]).Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure is Snail.Sdk.Mcp's envelope: a field with nothing to say is left out rather than written null, and the
    /// text is all of it — no structured content beside it for a client to check against a schema.
    /// </summary>
    [Fact]
    public void From_DiagnosisThatFailedWithoutAHint_IsTheEnvelopeAloneWithNoNullHint()
    {
        var result = ToolResponse.From(Answer.Failed<Readiness>("The Docker daemon at unix:///var/run/docker.sock did not answer."));

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.Equal("""{"ok":false,"error":"The Docker daemon at unix:///var/run/docker.sock did not answer."}""", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    /// <summary>An image block has nowhere to carry a name, and a nameless chart cannot be found or passed on by what it is called.</summary>
    [Fact]
    public async Task Run_PictureInOut_ComesBackAsAResourceThatKeepsItsName()
    {
        var result = await RunAsync(Answer.Ok(Ran with { Outputs = [new OutputFile("chart.png", "image/png", [137, 80, 78, 71])] }));

        var resource = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(result.Content[1]).Resource);
        Assert.Equal("sandbox://out/chart.png", resource.Uri);
        Assert.Equal("image/png", resource.MimeType);
        Assert.Equal(Convert.ToBase64String([137, 80, 78, 71]), System.Text.Encoding.ASCII.GetString(resource.Blob.Span));
    }

    /// <summary>A block with a media type and a base64 body is what the platform takes aside into its object store.</summary>
    [Fact]
    public async Task Run_TableInOut_ComesBackAsAResourceNamedByItsPlace()
    {
        var result = await RunAsync(Answer.Ok(Ran with { Outputs = [new OutputFile("by month/sales.csv", "text/csv", [97, 44, 98])] }));

        var resource = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(result.Content[1]).Resource);
        Assert.Equal("sandbox://out/by%20month/sales.csv", resource.Uri);
        Assert.Equal("text/csv", resource.MimeType);
    }

    [Fact]
    public async Task Run_RunKilledAtItsTimeout_SaysNothingFromOutCameBack() =>
        Assert.Equal(Messages.TimedOut, Data(await RunAsync(Answer.Ok(Ran with { ExitCode = null, HasTimedOut = true }))).GetProperty("note").GetString());

    [Fact]
    public async Task Run_RunKilledForItsMemory_SaysSo() =>
        Assert.Equal(Messages.Killed, Data(await RunAsync(Answer.Ok(Ran with { ExitCode = 137 }))).GetProperty("note").GetString());

    /// <summary>The text is what the model reads: the files by name and size, never their bodies, which travel as blocks.</summary>
    [Fact]
    public async Task Run_AnyRun_ListsItsFilesInTheTextWithoutTheirBodies()
    {
        var result = await RunAsync(Answer.Ok(Ran with { Outputs = [new OutputFile("chart.png", "image/png", new byte[4096])] }));

        var file = Assert.Single(Data(result).GetProperty("files").EnumerateArray().ToList());
        Assert.Equal("chart.png", file.GetProperty("name").GetString());
        Assert.Equal(4096, file.GetProperty("bytes").GetInt32());
        Assert.False(file.TryGetProperty("content", out _));
        Assert.True(Assert.IsType<TextContentBlock>(result.Content[0]).Text.Length < 1024);
    }

    /// <summary>A client that reads the job later has nothing but its report, so the result carries every file whole.</summary>
    [Fact]
    public async Task Run_RunWithFiles_CarriesThemBase64InTheJobsResult()
    {
        var report = await EndedAsync(Answer.Ok(Ran with { Outputs = [new OutputFile("chart.png", "image/png", [137, 80, 78, 71])] }));

        var file = Assert.Single(report.Result!.Value.GetProperty("files").EnumerateArray().ToList());
        Assert.Equal(Convert.ToBase64String([137, 80, 78, 71]), file.GetProperty("content").GetString());
    }

    /// <summary>A run that could not happen is no run: its job ends failed with the refusal's words, never succeeded with a failure as its result.</summary>
    [Fact]
    public async Task Run_RunThatCouldNotHappen_EndsItsJobFailedWithTheRefusalsHint()
    {
        var report = await EndedAsync(Answer.Failed<RunOutcome>("There is no code to run.", "Pass the whole program in code."));

        Assert.Equal(JobState.Failed, report.State);
        Assert.Equal("There is no code to run.", report.Message);
        Assert.Equal("Pass the whole program in code.", report.Hint);
    }

    /// <summary>A run still going answers with its job, not with a result and not as an error, and the report rides beside the text.</summary>
    [Fact]
    public void Run_JobStillRunning_AnswersWhereToReadIt()
    {
        var report = new JobReport("job://sandbox/python/abc", JobState.Running, Now, null, 0.5, "restoring", null);

        var result = ToolResponse.Run(report);

        Assert.NotEqual(true, result.IsError);
        Assert.True(result.IsOk(), result.Text());
        Assert.Equal("job://sandbox/python/abc", Data(result).GetProperty("uri").GetString());
        Assert.Equal("running", Data(result).GetProperty("state").GetString());
        Assert.Equal(Messages.Running, Data(result).GetProperty("note").GetString());
        Assert.Equal("job://sandbox/python/abc", result.StructuredContent!.Value.GetProperty("uri").GetString());
    }

    /// <summary>A job the server stopped under — shutting down, cancelled — ended with no answer, and that is the sandbox's fault, not the program's.</summary>
    [Fact]
    public void Run_JobCancelled_IsAToolErrorThatSaysSo()
    {
        var report = new JobReport("job://sandbox/python/abc", JobState.Cancelled, Now, Now, null, "cancelled", null);

        var result = ToolResponse.Run(report);

        Assert.True(result.IsError);
        Assert.Contains("cancelled", Assert.IsType<TextContentBlock>(result.Content[0]).Text, StringComparison.Ordinal);
    }

    /// <summary>The run told as the tool tells it once its job ended within the wait, the outcome handed over in hand.</summary>
    private static async Task<CallToolResult> RunAsync(Answer<RunOutcome> answer) =>
        ToolResponse.Run(await EndedAsync(answer), answer);

    private static async Task<CallToolResult> TestsAsync(Answer<TestReport> answer) =>
        ToolResponse.Tests(await Ended("tests", answer.Map(AnswerFields.Of), ToolResponse.TestResult));

    /// <summary>The report a run's job ends with, through Snail.Sdk.Mcp's jobs as the tool starts it.</summary>
    private static Task<JobReport> EndedAsync(Answer<RunOutcome> answer) =>
        Ended("python", answer.Map(outcome => AnswerFields.Of(outcome, withContent: true)), ToolResponse.RunResult);

    private static Task<JobReport> Ended<T>(string kind, Answer<T> answer, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        new Jobs("sandbox").Start(kind, null, (_, _) => Task.FromResult(answer), type).Completion;

    private static JsonElement Data(CallToolResult result) =>
        JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text).RootElement.GetProperty("data");
}
