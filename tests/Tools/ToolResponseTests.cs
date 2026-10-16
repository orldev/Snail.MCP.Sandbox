using System.Text.Json;
using ModelContextProtocol.Protocol;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Domain;
using Snail.MCP.Sandbox.Tools;

namespace Snail.MCP.Sandbox.Tests.Tools;

public sealed class ToolResponseTests
{
    private static readonly RunOutcome Ran = new(0, "rows: 12\n", string.Empty, TimeSpan.FromSeconds(1.234));

    /// <summary>The program failing is still a run that happened; only a run that could not happen is a protocol error.</summary>
    [Fact]
    public void From_ProgramExitsWithAnError_IsNotAToolError()
    {
        var result = ToolResponse.From(Answer.Ok(Ran with { ExitCode = 1, Stderr = "KeyError: 'total'" }));

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
    public void From_TestReport_AnswersTheHostsTheRestoreReachedOrNullWhenNobodyWatched()
    {
        var watched = ToolResponse.From(Answer.Ok(new TestReport(Toolchain.Python, TestOutcome.Passed, TimeSpan.FromSeconds(3)) { Contacted = ["pypi.org", "files.pythonhosted.org"] }));
        var unwatched = ToolResponse.From(Answer.Ok(new TestReport(Toolchain.Python, TestOutcome.Passed, TimeSpan.FromSeconds(3))));

        Assert.Equal(["pypi.org", "files.pythonhosted.org"], Data(watched).GetProperty("network").EnumerateArray().Select(host => host.GetString()));
        Assert.Equal(JsonValueKind.Null, Data(unwatched).GetProperty("network").ValueKind);
    }

    [Fact]
    public void From_RunThatCouldNotHappen_IsAToolErrorWithItsHint()
    {
        var result = ToolResponse.From(Answer.Failed<RunOutcome>("The sandbox node has no runsc runtime.", "Say so in the answer."));

        Assert.True(result.IsError);
        Assert.Contains("Say so in the answer.", Assert.IsType<TextContentBlock>(result.Content[0]).Text, StringComparison.Ordinal);
    }

    /// <summary>An image block has nowhere to carry a name, and a nameless chart cannot be found or passed on by what it is called.</summary>
    [Fact]
    public void From_PictureInOut_ComesBackAsAResourceThatKeepsItsName()
    {
        var result = ToolResponse.From(Answer.Ok(Ran with { Outputs = [new OutputFile("chart.png", "image/png", [137, 80, 78, 71])] }));

        var resource = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(result.Content[1]).Resource);
        Assert.Equal("sandbox://out/chart.png", resource.Uri);
        Assert.Equal("image/png", resource.MimeType);
    }

    /// <summary>A block with a media type and a base64 body is what the platform takes aside into its object store.</summary>
    [Fact]
    public void From_TableInOut_ComesBackAsAResourceNamedByItsPlace()
    {
        var result = ToolResponse.From(Answer.Ok(Ran with { Outputs = [new OutputFile("by month/sales.csv", "text/csv", [97, 44, 98])] }));

        var resource = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(result.Content[1]).Resource);
        Assert.Equal("sandbox://out/by%20month/sales.csv", resource.Uri);
        Assert.Equal("text/csv", resource.MimeType);
    }

    [Fact]
    public void From_RunKilledAtItsTimeout_SaysNothingFromOutCameBack() =>
        Assert.Equal(Messages.TimedOut, Data(ToolResponse.From(Answer.Ok(Ran with { ExitCode = null, HasTimedOut = true }))).GetProperty("note").GetString());

    [Fact]
    public void From_RunKilledForItsMemory_SaysSo() =>
        Assert.Equal(Messages.Killed, Data(ToolResponse.From(Answer.Ok(Ran with { ExitCode = 137 }))).GetProperty("note").GetString());

    [Fact]
    public void From_AnyRun_ListsItsFilesInTheTextWithoutTheirBodies()
    {
        var result = ToolResponse.From(Answer.Ok(Ran with { Outputs = [new OutputFile("chart.png", "image/png", new byte[4096])] }));

        var file = Assert.Single(Data(result).GetProperty("files").EnumerateArray().ToList());
        Assert.Equal("chart.png", file.GetProperty("name").GetString());
        Assert.Equal(4096, file.GetProperty("bytes").GetInt32());
        Assert.True(Assert.IsType<TextContentBlock>(result.Content[0]).Text.Length < 1024);
    }

    private static JsonElement Data(CallToolResult result) =>
        JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text).RootElement.GetProperty("data");
}
