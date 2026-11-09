using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>The server the in-memory tests serve reads none of the settings of the machine that runs them.</summary>
/// <remarks>
/// It was built by the program, which read the developer's settings file, <c>~/.config/s3l-mcp-sandbox/config.json</c>,
/// and their <c>SNAIL_MCP_SANDBOX_</c> variables: a suite green on the CI could fail, or pass for the wrong reason, on a
/// machine that runs the server. The machine's settings are stood in for by a file the variable names and a variable of
/// the test's process, set only while the server is built, with no other test running beside it.
/// </remarks>
[Collection(SettingsOfTheMachine.Name)]
public sealed class SandboxServerIsolationTests
{
    [Fact]
    public async Task Options_WithSettingsOnTheMachine_AreTheDefaults()
    {
        var folder = TestFolder.Create();
        var file = Path.Combine(folder, "config.json");
        await File.WriteAllTextAsync(file, """{ "Image": "from-the-developers-file:latest" }""");
        Environment.SetEnvironmentVariable("SNAIL_MCP_SANDBOX_CONFIG", file);
        Environment.SetEnvironmentVariable("SNAIL_MCP_SANDBOX_DEFAULT_TIMEOUT_SECONDS", "59");

        var server = new SandboxServer();

        try
        {
            await server.InitializeAsync();

            var options = server.Services.GetRequiredService<IOptions<SandboxOptions>>().Value;

            Assert.Equal(new SandboxOptions().Image, options.Image);
            Assert.Equal(new SandboxOptions().DefaultTimeoutSeconds, options.DefaultTimeoutSeconds);
        }
        finally
        {
            await server.DisposeAsync();
            Environment.SetEnvironmentVariable("SNAIL_MCP_SANDBOX_CONFIG", null);
            Environment.SetEnvironmentVariable("SNAIL_MCP_SANDBOX_DEFAULT_TIMEOUT_SECONDS", null);
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>The tests that stand in for the machine's settings by changing the process's variables, run with no other test beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SettingsOfTheMachine
{
    /// <summary>The collection's name.</summary>
    public const string Name = "settings of the machine";
}
