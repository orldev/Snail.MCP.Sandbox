using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Snail.Sdk.Mcp.Hosting.Jobs;
using Snail.Sdk.Mcp.JobClient;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>The server as the platform reaches it: over HTTP, at /mcp, with a health check Nomad polls.</summary>
public sealed class HttpContractTests(SandboxServer factory) : IClassFixture<SandboxServer>
{
    /// <summary>A page in a browser always sends an Origin and an agent's client never does: the refusal is Snail.Sdk.Mcp.Http's, kept by taking its host.</summary>
    [Fact]
    public async Task Request_FromABrowser_IsRefused()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/mcp", UriKind.Relative));
        request.Headers.Add("Origin", "https://example.com");

        using var answer = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, answer.StatusCode);
    }

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
        Assert.Contains("runsc", Assert.IsType<TextContentBlock>(result.Content[0]).Text, StringComparison.Ordinal);
    }

    /// <summary>A model that does not know the names asks in its own words, and one call answers with the name and the arguments.</summary>
    [Fact]
    public async Task CallTool_FindTool_NamesTheToolForATaskInTheModelsOwnWords()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        var result = await session.CallToolAsync("find_tool", new Dictionary<string, object?> { ["task"] = "draw a chart from this table" });
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("sandbox_run_python", text, StringComparison.Ordinal);
        Assert.Contains("timeoutSeconds", text, StringComparison.Ordinal);
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
    /// A client that cannot wait asks with wait 0 and gets the job; one that can awaits its resource and finds the same
    /// answer in the result, the files whole inside it — that is all a late reader has.
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
        var file = ended.Result!.Value.GetProperty("data").GetProperty("files")[0];
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

    private static Task<McpClient> SessionAsync(HttpClient http) =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") },
            http,
            ownsHttpClient: false));
}
