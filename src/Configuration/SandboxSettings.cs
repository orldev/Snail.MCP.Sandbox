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

    /// <summary>Where the server listens, <c>SNAIL_MCP_SANDBOX_URL</c>: only its port is read, on every interface of the container.</summary>
    public static Uri Url => UrlOf(Read());

    /// <summary>The address <paramref name="settings"/> name, or the default; a value that is not an http address is refused naming the variable.</summary>
    /// <remarks>Read before a host exists, so it cannot fail through the options' validation like every other setting; the refusal is worded the same way instead of being a bare UriFormatException.</remarks>
    public static Uri UrlOf(IConfiguration settings)
    {
        var named = settings["Url"] ?? "http://0.0.0.0:8826/";

        if (Uri.TryCreate(named, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps))
            return address;

        throw new InvalidOperationException($"{Prefix}URL must be an absolute http address, such as http://0.0.0.0:8826/; it was '{named}'.");
    }

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
