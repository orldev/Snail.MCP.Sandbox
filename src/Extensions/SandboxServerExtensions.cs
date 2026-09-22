using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Tools;

namespace Snail.MCP.Sandbox.Extensions;

/// <summary>Composition root: which features the server has, and where it answers.</summary>
public static class SandboxServerExtensions
{
    extension(WebApplicationBuilder builder)
    {
        public WebApplicationBuilder ConfigureSandboxServer()
        {
            builder.Services
                .AddOptions<SandboxOptions>()
                .Bind(builder.Configuration.GetSection(SandboxOptions.SectionName))
                .ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<SandboxOptions>, SandboxOptionsValidation>();

            builder.Services.AddRuns();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<SessionGrant>();
            builder.Services.AddSingleton<ToolIndex>();

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation { Name = "snail-sandbox", Version = "0.1.0" };
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
