using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Tools;
using Snail.Sdk.Mcp.Hosting;
using Snail.Sdk.Mcp.Hosting.Discovery;
using Snail.Sdk.Mcp.Hosting.Jobs;
using Snail.Sdk.Mcp.Hosting.Sessions;
using Snail.Sdk.Mcp.Http;

namespace Snail.MCP.Sandbox.Extensions;

/// <summary>Composition root: which features the server has, and where it answers.</summary>
public static class SandboxServerExtensions
{
    /// <summary>
    /// The words a caller reaches for that a tool is not named with and its description need not carry, for <c>find_tool</c>.
    /// They live here rather than in the descriptions because the descriptions are read by every session whether or not
    /// anyone searches, and these are read only when someone does; a guard test keeps them the tools that exist.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Meanings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sandbox_run_python"] =
            "execute script compute calculate analyse chart plot graph figure table report convert parse "
            + "pandas numpy matplotlib duckdb openpyxl excel csv json dataframe statistics fit model",
        ["sandbox_run_tests"] =
            "test tests unit build compile verify green red failing passing ci restore pytest xunit nunit mstest jest "
            + "npm dotnet solution csproj project suite regression",
        ["sandbox_diagnose"] =
            "health ready broken failing available runtime docker daemon image node check why",
        ["find_tool"] = "search discover name parameters arguments how"
    };

    extension(WebApplicationBuilder builder)
    {
        /// <summary>The tools, the run and test services, and the settings, read from the settings file and the <c>SNAIL_MCP_SANDBOX_</c> variables only.</summary>
        /// <remarks>
        /// The host is Snail.Sdk.Mcp.Http's for the endpoint of <see cref="SandboxSettings.Endpoint"/> — every interface in the
        /// container, log lines on stderr, where the container's log driver collects them, the heap trimmed when idle. A tool
        /// that throws answers in the envelope; a session's grant (<c>?tool=</c>) is held at the door and hides what it
        /// withholds; <c>find_tool</c> searches what the session may call; a job belongs to the MCP session that started it
        /// (<c>JobOwners.Session</c>), so a session reads and watches only the runs it started.
        /// </remarks>
        public WebApplicationBuilder ConfigureSandboxServer()
        {
            builder.Services
                .AddOptions<SandboxOptions>()
                .Bind(SandboxSettings.Read())
                .ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<SandboxOptions>, SandboxOptionsValidation>();

            builder.Services.AddRuns();

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = ServerIdentity.Of(typeof(SandboxServerExtensions).Assembly);
                    options.ServerInstructions = ServerGuidance.Instructions;
                })
                .ExplainFailures()
                .WithLocalHttpTransport()
                .WithSessionGrant()
                .WithTools<RunTools>(ToolJson.Options(SandboxJson.Default))
                .WithToolDiscovery(Discovery)
                .WithJobs(SandboxSettings.Name, JobOwners.Session);

            return builder;
        }
    }

    /// <summary>How <c>find_tool</c> reads this server's tools: the prefix every one of its own shares, and the other words each is asked for by.</summary>
    internal static void Discovery(DiscoveryOptions options)
    {
        options.Prefix = "sandbox_";

        foreach (var (tool, words) in Meanings)
            options.Meanings[tool] = words;
    }

    extension(WebApplication app)
    {
        /// <summary>The MCP endpoint, behind the refusal of a browser's request, and the health check Nomad polls.</summary>
        public WebApplication UseSandboxEndpoints()
        {
            app.MapLocalMcp();
            app.MapHealth();

            return app;
        }
    }
}
