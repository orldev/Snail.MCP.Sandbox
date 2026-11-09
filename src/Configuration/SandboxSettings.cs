using System.Collections;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Sandbox.Configuration;

/// <summary>The settings <see cref="SandboxOptions"/> is bound from: the server's settings file, then its <c>SNAIL_MCP_SANDBOX_</c> variables, which win.</summary>
/// <remarks>
/// The file is the first of Snail.Sdk's chain, <c>~/.config/s3l-mcp-sandbox/config.json</c> among it, and the reading is
/// the same in every Snail server; only the name is this server's. The settings are built apart from the web host's own
/// configuration on purpose: an <c>appsettings.json</c> or a variable of whoever started the server that happens to share
/// a name is not a setting of this server and must not become one by accident.
/// </remarks>
public static class SandboxSettings
{
    /// <summary>The server's short name, which names its settings file and the prefix of its variables.</summary>
    public const string Name = "sandbox";

    /// <summary>What every variable this server reads begins with.</summary>
    public static string Prefix { get; } = ServerSettings.PrefixOf(Name);

    /// <summary>Where the server listens when <c>SNAIL_MCP_SANDBOX_URL</c> names nothing: port 8826, on loopback on a machine and on every interface in a container.</summary>
    public static readonly Uri DefaultUrl = new("http://127.0.0.1:8826/");

    /// <summary>How far the endpoint may reach: loopback on a machine, every interface inside a container, whose published port is the boundary.</summary>
    /// <remarks>
    /// The server holds no key: whoever reaches its port runs code on the node. So it is <see cref="McpReach.LoopbackOrContainer"/>,
    /// and the default is loopback — at <c>0.0.0.0</c>, the reach would refuse the default itself outside a container.
    /// The server's name goes with it, so a refusal names the variable even where the entry assembly is not the server's — a test runner.
    /// </remarks>
    private static readonly McpEndpointOptions Reach = new() { Reach = McpReach.LoopbackOrContainer, Server = Name };

    /// <summary>The endpoint <paramref name="settings"/> name, over HTTP, the only transport this server has.</summary>
    /// <remarks>
    /// Snail.Sdk.Mcp's reading with the reach <see cref="McpReach.LoopbackOrContainer"/>: inside the container — the
    /// image's <c>DOTNET_RUNNING_IN_CONTAINER</c> — the port opens on every interface, since the container's published port
    /// is the boundary, whatever host the address names; outside one it stays on loopback, and an address at
    /// <c>0.0.0.0</c> or beyond loopback is refused at the start naming the variable. Read before a host exists, so a value
    /// that is not an http address, or a transport or port that would mean stdio, is refused at the start naming the key,
    /// rather than through the options' validation.
    /// </remarks>
    public static McpEndpoint EndpointOf(IConfiguration settings) => McpEndpoint.ReadHttp(settings, DefaultUrl, Reach);

    /// <summary>The settings as they stand: the settings file found, then the process's variables.</summary>
    public static IConfiguration Read() => Read(ServerSettings.FileOf(Name), Environment.GetEnvironmentVariables());

    /// <summary>The server's variables among <paramref name="variables"/>, with no settings file.</summary>
    public static IConfiguration Read(IDictionary variables) => Read(null, variables);

    /// <summary>The settings of <paramref name="file"/>, when there is one, under the server's variables among <paramref name="variables"/>.</summary>
    public static IConfiguration Read(string? file, IDictionary variables) =>
        new ConfigurationBuilder()
            .AddServerSettings(Name, file, variables)
            .Build();
}
