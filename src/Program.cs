using Snail.MCP.Sandbox.Extensions;

var app = WebApplication.CreateBuilder(args)
    .ConfigureSandboxServer()
    .Build()
    .UseSandboxEndpoints();

await app.RunAsync();

/// <summary>The entry point, named so the contract tests can host the server in memory.</summary>
public partial class Program;
