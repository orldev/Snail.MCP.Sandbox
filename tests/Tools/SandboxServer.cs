using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>The whole server in memory, with containers that answer a canned run instead of the machine's real Docker daemon.</summary>
public sealed class SandboxServer : WebApplicationFactory<Program>
{
    public static readonly RunOutcome Canned = new(0, "rows: 12\n", string.Empty, TimeSpan.FromSeconds(1))
    {
        Outputs = [new OutputFile("chart.png", "image/png", [137, 80, 78, 71])]
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            foreach (var sweep in services.Where(service => service.ImplementationType == typeof(LeftoverContainers)).ToList())
                services.Remove(sweep);

            services.AddSingleton<IContainers, CannedContainers>();
        });

    private sealed class CannedContainers : IContainers
    {
        public Task<Answer<RunOutcome>> RunAsync(string code, IReadOnlyList<InputFile> inputs, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(Answer.Ok(Canned));

        public Task<Answer<Readiness>> DiagnoseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Answer.Failed<Readiness>("The sandbox node has no runsc runtime, and no code runs there until it has."));
    }
}
