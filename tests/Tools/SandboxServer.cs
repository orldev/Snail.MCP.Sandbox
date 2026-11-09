using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;
using Snail.MCP.Sandbox.Extensions;
using Snail.Sdk.Mcp.Hosting;
using Snail.Sdk.Mcp.Http;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>The whole server in memory, with containers that answer a canned run instead of the machine's real Docker daemon.</summary>
/// <remarks>
/// Built as the program builds it, over settings of its own — no settings file and no variables — and served by
/// Snail.Sdk's <see cref="InProcessServer"/> on a free loopback port. As a <c>WebApplicationFactory&lt;Program&gt;</c> it
/// ran the program, which read the developer's <c>~/.config/s3l-mcp-sandbox/config.json</c> and
/// <c>SNAIL_MCP_SANDBOX_</c> variables into the server under test.
/// </remarks>
public sealed class SandboxServer : IAsyncLifetime
{
    public static readonly RunOutcome Canned = new(0, "rows: 12\n", string.Empty, TimeSpan.FromSeconds(1))
    {
        Outputs = [new OutputFile("chart.png", "image/png", [137, 80, 78, 71])]
    };

    private InProcessServer? _server;

    /// <summary>The server's services.</summary>
    public IServiceProvider Services => Started.Services;

    private InProcessServer Started => _server ?? throw new InvalidOperationException("The server is not started yet.");

    /// <summary>A client of the server, at its address.</summary>
    public HttpClient CreateClient() => new() { BaseAddress = Started.Address };

    public async Task InitializeAsync()
    {
        var settings = SandboxSettings.Read(new Hashtable());
        var endpoint = new McpEndpoint(McpTransport.Http, new Uri("http://127.0.0.1:0/"), ListensOnEveryInterface: false);
        var builder = McpHttpHost.CreateBuilder([], endpoint, settings).ConfigureSandboxServer(settings);

        foreach (var sweep in builder.Services.Where(service => service.ImplementationType == typeof(LeftoverContainers)).ToList())
            builder.Services.Remove(sweep);

        builder.Services.AddSingleton<IContainers, CannedContainers>();

        _server = await InProcessServer.ServeAsync(builder.Build().UseSandboxEndpoints());
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    private sealed class CannedContainers : IContainers
    {
        public Task<Answer<RunOutcome>> RunAsync(string code, IReadOnlyList<InputFile> inputs, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(Answer.Ok(Canned));

        public Task<Answer<Readiness>> DiagnoseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Answer.Failed<Readiness>("The sandbox node has no runsc runtime, and no code runs there until it has."));
    }
}
