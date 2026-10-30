using System.Reflection;
using System.Text.RegularExpressions;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Tests.Deploy;

/// <summary>
/// Guard tests between the Compose file and what it starts: a setting spelt wrong there is one the server never
/// reads, and a name that drifted from the code is a stack that comes up and refuses every run.
/// </summary>
public sealed partial class ComposeTests
{
    private static readonly string Compose = File.ReadAllText(Path.Combine(Repository.Root, "deploy", "compose.yml"));

    [Fact]
    public void Server_EverySetting_IsOneTheServerReads()
    {
        var known = typeof(SandboxOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(setting => setting.Name.ToUpperInvariant());
        var given = Setting().Matches(Compose).Select(match => Snail.Sdk.Mcp.Hosting.EnvironmentSettings.ToConfigurationKey(match.Groups["name"].Value)).ToList();

        Assert.NotEmpty(given);
        Assert.All(given, name => Assert.Contains(name, known));
    }

    [Fact]
    public void Server_PublishedPort_IsTheOneTheImageListensOn()
    {
        var image = File.ReadAllText(Path.Combine(Repository.Root, "deploy", "server", "Dockerfile"));
        var port = ListeningPort().Match(image).Groups["port"].Value;

        Assert.NotEmpty(port);
        Assert.Contains($":{port}\"", Compose, StringComparison.Ordinal);
    }

    /// <summary>The server attaches a restore container by name, so the name it is given is the one the internal network is created under.</summary>
    [Fact]
    public void Server_RestoreNetwork_IsTheInternalNetworkComposeCreates() =>
        Assert.Contains($"    name: {Value("SNAIL_MCP_SANDBOX_RESTORE_NETWORK")}\n    internal: true", Compose, StringComparison.Ordinal);

    /// <summary>
    /// By address, not by name: under runsc release-20260921.0 a restore container did not resolve the service's
    /// name on this network and every restore failed, while under runc on the same network it resolved.
    /// </summary>
    [Fact]
    public void Server_RestoreProxy_IsTheAddressAndThePortTheServerHolds() =>
        Assert.Equal($"http://{Value("ipv4_address")}:{Value("SNAIL_MCP_SANDBOX_RESTORE_PROXY_PORT")}", Value("SNAIL_MCP_SANDBOX_RESTORE_PROXY"));

    [Fact]
    public void Executor_Image_IsTheOneTheServerRunsCodeIn() =>
        Assert.Contains($"\n    image: {Value("SNAIL_MCP_SANDBOX_IMAGE")}\n", Compose, StringComparison.Ordinal);

    /// <summary>The server is told of no test image here, so the ones brought onto the host have to be its defaults.</summary>
    [Fact]
    public void Toolchains_Images_AreTheDefaultsATestRunStartsFrom()
    {
        var defaults = new SandboxOptions();

        Assert.All([defaults.DotnetImage, defaults.NodeImage], image => Assert.Contains($"\n    image: {image}\n", Compose, StringComparison.Ordinal));
    }

    /// <summary>The server's image is named by the shared image workflow from the repository; every other one by its job.</summary>
    [Fact]
    public void Images_BuiltHere_AreNamedAsTheWorkflowPushesThem()
    {
        var workflow = File.ReadAllText(Path.Combine(Repository.Root, ".github", "workflows", "images.yml"));
        var built = BuiltImage().Matches(Compose).Select(match => match.Groups["name"].Value).Distinct().ToList();

        Assert.NotEmpty(built);
        Assert.All(built, name => Assert.True(name == "s3l-sandbox-mcp" || workflow.Contains($"image: {name}\n", StringComparison.Ordinal), name));
    }

    /// <summary>The example is what an operator copies; a variable missing there is one they learn of from a failed start.</summary>
    [Fact]
    public void EnvExample_EveryVariableComposeReads_HasALine()
    {
        var example = File.ReadAllLines(Path.Combine(Repository.Root, "deploy", ".env.example"));
        var read = Variable().Matches(Compose).Select(match => match.Groups["name"].Value).Distinct().ToList();

        Assert.NotEmpty(read);
        Assert.All(read, name => Assert.Contains(example, line => line.StartsWith($"{name}=", StringComparison.Ordinal)));
    }

    private static string Value(string key) =>
        Compose.Split('\n')
            .Select(line => line.Trim())
            .Single(line => line.StartsWith($"{key}:", StringComparison.Ordinal))[(key.Length + 1)..]
            .Trim()
            .Trim('"');

    [GeneratedRegex(@"SNAIL_MCP_SANDBOX_(?<name>[A-Z_]+?)(?:__\d+)?:", RegexOptions.CultureInvariant)]
    private static partial Regex Setting();

    [GeneratedRegex(@"SNAIL_MCP_SANDBOX_URL=http://[^:/]+:(?<port>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ListeningPort();

    [GeneratedRegex(@"\$\{REGISTRY[^}]*\}/(?<name>[a-z0-9-]+):", RegexOptions.CultureInvariant)]
    private static partial Regex BuiltImage();

    [GeneratedRegex(@"\$\{(?<name>[A-Z_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();
}
