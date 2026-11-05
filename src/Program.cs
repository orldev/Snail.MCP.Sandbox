using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Extensions;
using Snail.Sdk.Mcp.Http;

var app = McpHttpHost.CreateContainerBuilder(args, SandboxSettings.Url.Port)
    .ConfigureSandboxServer()
    .Build()
    .UseSandboxEndpoints();

await app.RunAsync();
