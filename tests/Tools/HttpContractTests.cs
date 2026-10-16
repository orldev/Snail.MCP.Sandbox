using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>The server as the platform reaches it: over HTTP, at /mcp, with a health check Nomad polls.</summary>
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

        Assert.Equal("s3l-mcp-sandbox", session.ServerInfo.Name);
        Assert.Contains("no network", session.ServerInstructions, StringComparison.Ordinal);
    }

    /// <summary>The descriptions name code, files and timeoutSeconds; the schema the model fills in has to use the same names.</summary>
    [Fact]
    public async Task ListTools_OverStreamableHttp_OffersRunPythonWithTheParametersItsDescriptionsName()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        var tool = (await session.ListToolsAsync()).Single(listed => listed.Name == "sandbox_run_python");
        var parameters = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name);

        Assert.Equal("sandbox_run_python", tool.Name);
        Assert.Equal(["code", "files", "timeoutSeconds"], parameters.Order(StringComparer.Ordinal));
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

    private static Task<McpClient> SessionAsync(HttpClient http) =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") },
            http,
            ownsHttpClient: false));
}
