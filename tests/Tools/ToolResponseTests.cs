using System.Text.Json;
using ModelContextProtocol.Protocol;
using Snail.MCP.Sandbox.Application;
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
    public void Run_ProgramExitsWithAnError_IsNotAToolError()
    {
        var result = Run(Answer.Ok(Ran with { ExitCode = 1, Stderr = "KeyError: 'total'" }));

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
    public void Tests_TestReport_AnswersTheHostsTheRestoreReachedOrNullWhenNobodyWatched()
    {
        var watched = Tests(Answer.Ok(new TestReport(Toolchain.Python, TestOutcome.Passed, TimeSpan.FromSeconds(3)) { Contacted = ["pypi.org", "files.pythonhosted.org"] }));
        var unwatched = Tests(Answer.Ok(new TestReport(Toolchain.Python, TestOutcome.Passed, TimeSpan.FromSeconds(3))));

        Assert.Equal(["pypi.org", "files.pythonhosted.org"], Data(watched).GetProperty("network").EnumerateArray().Select(host => host.GetString()));
        Assert.Equal(JsonValueKind.Null, Data(unwatched).GetProperty("network").ValueKind);
    }

    [Fact]
    public void Run_RunThatCouldNotHappen_IsAToolErrorWithItsHint()
    {
        var result = Run(Answer.Failed<RunOutcome>("The sandbox node has no runsc runtime.", "Say so in the answer."));

        Assert.True(result.IsError);
        Assert.Contains("Say so in the answer.", Assert.IsType<TextContentBlock>(result.Content[0]).Text, StringComparison.Ordinal);
    }

    /// <summary>An image block has nowhere to carry a name, and a nameless chart cannot be found or passed on by what it is called.</summary>
    [Fact]
    public void Run_PictureInOut_ComesBackAsAResourceThatKeepsItsName()
    {
        var result = Run(Answer.Ok(Ran with { Outputs = [new OutputFile("chart.png", "image/png", [137, 80, 78, 71])] }));

        var resource = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(result.Content[1]).Resource);
        Assert.Equal("sandbox://out/chart.png", resource.Uri);
        Assert.Equal("image/png", resource.MimeType);
        Assert.Equal(Convert.ToBase64String([137, 80, 78, 71]), System.Text.Encoding.ASCII.GetString(resource.Blob.Span));
    }

    /// <summary>A block with a media type and a base64 body is what the platform takes aside into its object store.</summary>
    [Fact]
    public void Run_TableInOut_ComesBackAsAResourceNamedByItsPlace()
    {
        var result = Run(Answer.Ok(Ran with { Outputs = [new OutputFile("by month/sales.csv", "text/csv", [97, 44, 98])] }));

        var resource = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(result.Content[1]).Resource);
        Assert.Equal("sandbox://out/by%20month/sales.csv", resource.Uri);
        Assert.Equal("text/csv", resource.MimeType);
    }

    [Fact]
    public void Run_RunKilledAtItsTimeout_SaysNothingFromOutCameBack() =>
        Assert.Equal(Messages.TimedOut, Data(Run(Answer.Ok(Ran with { ExitCode = null, HasTimedOut = true }))).GetProperty("note").GetString());

    [Fact]
    public void Run_RunKilledForItsMemory_SaysSo() =>
        Assert.Equal(Messages.Killed, Data(Run(Answer.Ok(Ran with { ExitCode = 137 }))).GetProperty("note").GetString());

    /// <summary>The text is what the model reads: the files by name and size, never their bodies, which travel as blocks.</summary>
    [Fact]
    public void Run_AnyRun_ListsItsFilesInTheTextWithoutTheirBodies()
    {
        var result = Run(Answer.Ok(Ran with { Outputs = [new OutputFile("chart.png", "image/png", new byte[4096])] }));

        var file = Assert.Single(Data(result).GetProperty("files").EnumerateArray().ToList());
        Assert.Equal("chart.png", file.GetProperty("name").GetString());
        Assert.Equal(4096, file.GetProperty("bytes").GetInt32());
        Assert.False(file.TryGetProperty("content", out _));
        Assert.True(Assert.IsType<TextContentBlock>(result.Content[0]).Text.Length < 1024);
    }

    /// <summary>A client that reads the job later has nothing but its report, so the result carries every file whole.</summary>
    [Fact]
    public void Answered_RunWithFiles_CarriesThemBase64InTheJobsResult()
    {
        var answer = ToolResponse.Answered(Answer.Ok(Ran with { Outputs = [new OutputFile("chart.png", "image/png", [137, 80, 78, 71])] }));

        var file = Assert.Single(answer.Data!.Files);
        Assert.Equal(Convert.ToBase64String([137, 80, 78, 71]), file.Content);
    }

    /// <summary>A run still going answers with its job, not with a result and not as an error, and the report rides beside the text.</summary>
    [Fact]
    public void Run_JobStillRunning_AnswersWhereToReadIt()
    {
        var report = new JobReport("job://sandbox/python/abc", JobState.Running, Now, null, 0.5, "restoring", null);

        var result = ToolResponse.Run(report);

        var text = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text).RootElement;
        Assert.NotEqual(true, result.IsError);
        Assert.True(text.GetProperty("ok").GetBoolean());
        Assert.Equal("job://sandbox/python/abc", text.GetProperty("job").GetString());
        Assert.Equal("running", text.GetProperty("state").GetString());
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

    private static CallToolResult Run(Answer<RunOutcome> answer) =>
        ToolResponse.Run(Ended(JsonSerializer.SerializeToElement(ToolResponse.Answered(answer), ToolResponse.RunResult)));

    private static CallToolResult Tests(Answer<TestReport> answer) =>
        ToolResponse.Tests(Ended(JsonSerializer.SerializeToElement(ToolResponse.Answered(answer), ToolResponse.TestResult)));

    /// <summary>A job that ended within the wait, as the tool receives it.</summary>
    private static JobReport Ended(JsonElement result) =>
        new("job://sandbox/python/abc", JobState.Succeeded, Now, Now, 1, null, result);

    private static JsonElement Data(CallToolResult result) =>
        JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text).RootElement.GetProperty("data");
}
