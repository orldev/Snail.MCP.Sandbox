using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Tools;
using Snail.Sdk.Mcp.Hosting;
using Snail.Sdk.Mcp.Hosting.Jobs;
using Snail.Sdk.Mcp.Http;

namespace Snail.MCP.Sandbox.Extensions;

/// <summary>Composition root: which features the server has, and where it answers.</summary>
public static class SandboxServerExtensions
{
    extension(WebApplicationBuilder builder)
    {
        /// <summary>The tools, the run and test services, and the settings, read from the settings file and the <c>SNAIL_MCP_SANDBOX_</c> variables only.</summary>
        /// <remarks>
        /// The host is Snail.Sdk.Mcp.Http's for a container — every interface on the port of <see cref="SandboxSettings.Url"/>,
        /// log lines on stderr, where the container's log driver collects them, the heap trimmed when idle.
        /// </remarks>
        public WebApplicationBuilder ConfigureSandboxServer()
        {
            builder.Services
                .AddOptions<SandboxOptions>()
                .Bind(SandboxSettings.Read())
                .ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<SandboxOptions>, SandboxOptionsValidation>();

            builder.Services.AddRuns();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<SessionGrant>();
            builder.Services.AddSingleton<SessionOwner>();
            builder.Services.AddSingleton<ToolIndex>();

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = Snail.Sdk.Mcp.Hosting.ServerIdentity.Of(typeof(SandboxServerExtensions).Assembly);
                    options.ServerInstructions = ServerGuidance.Instructions;
                })
                .RefuseWhatWasNotGranted()
                .WithLocalHttpTransport()
                .WithTools<RunTools>(ToolJson.Options(SandboxJson.Default))
                .WithTools<DiscoveryTools>()
                .WithJobs(SandboxSettings.Name, OwnerOf);

            return builder;
        }
    }

    /// <summary>The owner of a request's jobs: its session, so a session reads and watches only the runs it started.</summary>
    private static string? OwnerOf(IServiceProvider services) => services.GetService<SessionOwner>()?.Current;

    extension(WebApplication app)
    {
        /// <summary>The MCP endpoint, behind the refusal of a browser's request, and the health check Nomad polls.</summary>
        public WebApplication UseSandboxEndpoints()
        {
            app.MapLocalMcp();
            app.MapGet("/healthz", () => Results.Ok());

            return app;
        }
    }
}
