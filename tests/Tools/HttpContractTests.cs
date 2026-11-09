using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Snail.Sdk.Mcp.Hosting;
using Snail.Sdk.Mcp.Hosting.Jobs;
using Snail.Sdk.Mcp.JobClient;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>The server as the platform reaches it: over HTTP, at /mcp, with a health check Nomad polls; in memory, beside a canned daemon.</summary>
public sealed class HttpContractTests(SandboxServer factory) : IClassFixture<SandboxServer>
{
    [Fact]
    public async Task Healthz_RunningServer_AnswersOk()
    {
        using var client = factory.CreateClient();

        var answer = await client.GetAsync(new Uri("/healthz", UriKind.Relative));

        Assert.True(answer.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Initialize_OverStreamableHttp_NamesTheServerAndItsGuidance()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        Assert.Equal("s3l-mcp-sandbox", session.ServerInfo?.Name);
        Assert.Contains("no network", session.ServerInstructions, StringComparison.Ordinal);
    }

    /// <summary>The descriptions name code, files, timeoutSeconds and wait; the schema the model fills in has to use the same names.</summary>
    [Fact]
    public async Task ListTools_OverStreamableHttp_OffersRunPythonWithTheParametersItsDescriptionsName()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        var tool = (await session.ListToolsAsync()).Single(listed => listed.Name == "sandbox_run_python");
        var parameters = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name);

        Assert.Equal("sandbox_run_python", tool.Name);
        Assert.Equal(["code", "files", "timeoutSeconds", "wait"], parameters.Order(StringComparer.Ordinal));
        Assert.Equal(["code"], tool.JsonSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
    }

    /// <summary>The platform's probe calls this tool, and only an error flag in the protocol turns a server that answers into one that is not healthy.</summary>
    [Fact]
    public async Task CallTool_DiagnoseOnANodeThatCannotRun_IsAToolError()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        var result = await session.CallToolAsync("sandbox_diagnose");

        Assert.True(result.IsError);
        Assert.Contains("runsc", result.Error(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The platform gives a role some of the tools by naming them in the address, <c>?tool=</c>: the session is offered
    /// those alone, and a call to another is refused in the envelope. The grant is Snail.Sdk.Mcp's; this is the contract
    /// that this server takes it.
    /// </summary>
    [Fact]
    public async Task Session_GrantedOneTool_IsOfferedItAloneAndRefusedTheRest()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http, "?tool=sandbox_diagnose");

        var listed = await session.ListToolsAsync();
        var refused = await session.CallToolAsync("sandbox_run_python", new Dictionary<string, object?> { ["code"] = "print(1)" });

        Assert.Equal(["sandbox_diagnose"], listed.Select(tool => tool.Name));
        Assert.True(refused.IsError);
        Assert.Contains("'sandbox_run_python' was not granted to this session, which may use sandbox_diagnose", refused.Error(), StringComparison.Ordinal);
    }

    /// <summary>An argument the tool does not take is refused with the one it probably meant, never dropped so that the run goes on without the limit asked for.</summary>
    [Fact]
    public async Task CallTool_RunPythonWithAnArgumentItDoesNotTake_IsRefusedNamingTheOneMeant()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        var refused = await session.CallToolAsync("sandbox_run_python", new Dictionary<string, object?> { ["code"] = "print(1)", ["timeout"] = 5 });

        Assert.True(refused.IsError);
        Assert.Equal(ToolFailures.Arguments, refused.Type());
        Assert.Contains("timeoutSeconds", refused.Text(), StringComparison.Ordinal);
    }

    /// <summary>A model that does not know the names asks in its own words, and one call answers with the name and the arguments.</summary>
    [Fact]
    public async Task CallTool_FindTool_NamesTheToolForATaskInTheModelsOwnWords()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        var result = await session.CallToolAsync("find_tool", new Dictionary<string, object?> { ["task"] = "draw a chart from this table" });

        Assert.True(result.IsOk(), result.Text());
        Assert.Contains("sandbox_run_python", result.Text(), StringComparison.Ordinal);
        Assert.Contains("timeoutSeconds", result.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RunPython_ReturnsTheOutputAndEachFileAsItsOwnBlock()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        var result = await session.CallToolAsync("sandbox_run_python", new Dictionary<string, object?> { ["code"] = "print('rows: 12')" });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("rows: 12", Assert.IsType<TextContentBlock>(result.Content[0]).Text, StringComparison.Ordinal);
        Assert.Equal("sandbox://out/chart.png", Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(result.Content[1]).Resource).Uri);
    }

    /// <summary>
    /// A client that cannot wait asks with wait 0 and gets the job; one that can awaits its resource and finds the run as
    /// the result, the files whole inside it — that is all a late reader has.
    /// </summary>
    [Fact]
    public async Task CallTool_RunPythonWithNoWait_AnswersWithAJobWhoseResultCarriesTheFiles()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        var started = await session.CallToolAsync("sandbox_run_python", new Dictionary<string, object?> { ["code"] = "print('rows: 12')", ["wait"] = 0 });
        var report = JobReport.FromJson(started.StructuredContent!.Value.GetRawText())!;
        var ended = await session.AwaitJobAsync(report.Uri);

        Assert.StartsWith("job://sandbox/python/", report.Uri, StringComparison.Ordinal);
        Assert.Equal(JobState.Succeeded, ended.State);
        var file = ended.Result!.Value.GetProperty("files")[0];
        Assert.Equal("chart.png", file.GetProperty("name").GetString());
        Assert.Equal(Convert.ToBase64String([137, 80, 78, 71]), file.GetProperty("content").GetString());
    }

    /// <summary>The job's resource is where a model reads a run it did not wait for.</summary>
    [Fact]
    public async Task ReadResource_JobOfARun_IsItsReport()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        var started = await session.CallToolAsync("sandbox_run_python", new Dictionary<string, object?> { ["code"] = "print(1)", ["wait"] = 0 });
        var uri = JobReport.FromJson(started.StructuredContent!.Value.GetRawText())!.Uri;
        var read = await session.ReadJobAsync(uri);

        Assert.Equal(uri, read.Uri);
    }

    /// <summary>A session on the in-memory server, at the address the platform would open it at.</summary>
    internal static Task<McpClient> SessionAsync(HttpClient http, string query = "") =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, $"/mcp{query}") },
            http,
            ownsHttpClient: false));
}
