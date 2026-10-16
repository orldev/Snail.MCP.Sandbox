using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Tools;

namespace Snail.MCP.Sandbox.Extensions;

/// <summary>Composition root: which features the server has, and where it answers.</summary>
public static class SandboxServerExtensions
{
    extension(WebApplicationBuilder builder)
    {
        /// <summary>The tools, the run and test services, and the settings, read from the settings file and the <c>SNAIL_MCP_SANDBOX_</c> variables only.</summary>
        /// <remarks>Logging stays the web host's own, on standard output, where the container's log driver collects it.</remarks>
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
            builder.Services.AddSingleton<ToolIndex>();

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = Snail.Sdk.Mcp.Hosting.ServerIdentity.Of(typeof(SandboxServerExtensions).Assembly);
                    options.ServerInstructions = ServerGuidance.Instructions;
                })
                .RefuseWhatWasNotGranted()
                .WithHttpTransport()
                .WithTools<RunTools>()
                .WithTools<DiscoveryTools>();

            return builder;
        }
    }

    extension(WebApplication app)
    {
        /// <summary>The MCP endpoint and the health check Nomad polls.</summary>
        public WebApplication UseSandboxEndpoints()
        {
            app.MapMcp("/mcp");
            app.MapGet("/healthz", () => Results.Ok());

            return app;
        }
    }
}
