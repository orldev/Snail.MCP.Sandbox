using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Extensions;
using Snail.Sdk.Mcp.Hosting;
using Snail.Sdk.Mcp.Http;

// A setting the operator got wrong is said in one line with exit code 1, not as a crash with a trace and 134.
try
{
    var settings = SandboxSettings.Read();
    var app = McpHttpHost.CreateBuilder(args, SandboxSettings.EndpointOf(settings), settings)
        .ConfigureSandboxServer(settings)
        .Build()
        .UseSandboxEndpoints();

    await app.RunAsync();

    return 0;
}
catch (StartRefusedException refused)
{
    await Console.Error.WriteLineAsync(refused.Message);

    return 1;
}
