using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Snail.MCP.Sandbox.Tests.Tools;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>The wait a call asks for, against a number no delay can hold.</summary>
public sealed class WaitChaosTests(SandboxServer factory) : IClassFixture<SandboxServer>
{
    /// <summary>
    /// Breaks the wait: the description says from 0 to the run's own timeout, and nothing holds the call to it. A model that
    /// writes a large number to mean «as long as it takes» — ten million seconds — gets an exception from the delay, told as
    /// the SDK's generic error, with no hint that names wait.
    /// </summary>
    [Fact]
    public async Task CallTool_WaitFarPastAnyTimeout_IsAnsweredOrRefusedNamingWait()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        var result = await session.CallToolAsync("sandbox_run_python", new Dictionary<string, object?> { ["code"] = "print(1)", ["wait"] = 10_000_000 });
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;

        Assert.True(result.IsError != true || text.Contains("wait", StringComparison.Ordinal), text);
    }

    private static Task<McpClient> SessionAsync(HttpClient http) =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") },
            http,
            ownsHttpClient: false));
}
