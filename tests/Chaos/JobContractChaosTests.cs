using ModelContextProtocol;
using ModelContextProtocol.Client;
using Snail.MCP.Sandbox.Tests.Tools;
using Snail.Sdk.Mcp.Hosting.Jobs;
using Snail.Sdk.Mcp.JobClient;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>The jobs as the platform's many tasks share them, through one server.</summary>
public sealed class JobContractChaosTests(SandboxServer factory) : IClassFixture<SandboxServer>
{
    /// <summary>
    /// Breaks the retention: started with no owner, every session's job counted as one owner's, and the server kept the last
    /// twenty ended jobs in all — twenty sessions ending their runs after mine forgot my result before I read it, the very
    /// failure the SDK keeps jobs per owner against. A job belongs to its session now, and the twenty are counted per session.
    /// </summary>
    [Fact]
    public async Task ReadResource_JobEndedBeforeTwentyOfOtherSessions_IsStillReadable()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);

        var mine = await StartedAsync(session);
        await session.AwaitJobAsync(mine);
        foreach (var _ in Enumerable.Range(0, Jobs.Kept))
        {
            await using var other = await SessionAsync(http);
            await other.AwaitJobAsync(await StartedAsync(other));
        }

        var read = await session.ReadJobAsync(mine);

        Assert.Equal(JobState.Succeeded, read.State);
    }

    /// <summary>A job's URI is random, and still not a key: a session that somehow holds another's URI is told there is no such resource.</summary>
    [Fact]
    public async Task ReadResource_JobOfAnotherSession_IsUnknown()
    {
        using var http = factory.CreateClient();
        await using var session = await SessionAsync(http);
        await using var other = await SessionAsync(http);

        var mine = await StartedAsync(session);
        await session.AwaitJobAsync(mine);

        await Assert.ThrowsAsync<McpProtocolException>(() => other.ReadJobAsync(mine));
    }

    private static async Task<string> StartedAsync(McpClient session)
    {
        var started = await session.CallToolAsync("sandbox_run_python", new Dictionary<string, object?> { ["code"] = "print(1)", ["wait"] = 0 });

        return JobReport.FromJson(started.StructuredContent!.Value.GetRawText())!.Uri;
    }

    private static Task<McpClient> SessionAsync(HttpClient http) =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") },
            http,
            ownsHttpClient: false));
}
