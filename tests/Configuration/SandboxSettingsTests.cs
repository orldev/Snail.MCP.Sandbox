using System.Collections;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Tests.Configuration;

/// <summary>Every setting reaches the server from its settings file or its <c>SNAIL_MCP_SANDBOX_</c> variable, and nothing else is read.</summary>
public sealed class SandboxSettingsTests
{
    [Fact]
    public void Read_EverySettingAsAVariable_BindsNoneOfTheDefaults()
    {
        var defaults = new SandboxOptions();
        var variables = new Hashtable();

        foreach (var setting in typeof(SandboxOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            variables[Variable(setting.Name, setting.PropertyType == typeof(string[]))] = Sample(setting.PropertyType);

        var bound = new SandboxOptions();

        SandboxSettings.Read(variables).Bind(bound);

        Assert.All(
            typeof(SandboxOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            setting => Assert.NotEqual(Shown(setting.GetValue(defaults)), Shown(setting.GetValue(bound))));
    }

    /// <summary>The address is read before a host exists, so it cannot fail through the options' validation; the refusal still names the variable rather than throwing a bare format error.</summary>
    [Fact]
    public void UrlOf_NotAnAddress_IsRefusedNamingTheVariable()
    {
        var variables = new Hashtable { ["SNAIL_MCP_SANDBOX_URL"] = "eight thousand" };

        var refusal = Assert.Throws<InvalidOperationException>(() => SandboxSettings.UrlOf(SandboxSettings.Read(variables)));

        Assert.Contains("SNAIL_MCP_SANDBOX_URL", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UrlOf_NothingNamed_IsTheDefaultPort() =>
        Assert.Equal(8826, SandboxSettings.UrlOf(SandboxSettings.Read(new Hashtable())).Port);

    [Fact]
    public void Read_AnUnprefixedVariable_IsNotASetting()
    {
        var variables = new Hashtable { ["Snail__DockerEndpoint"] = "unix:///elsewhere.sock", ["DOCKER_ENDPOINT"] = "unix:///elsewhere.sock" };

        var bound = new SandboxOptions();

        SandboxSettings.Read(variables).Bind(bound);

        Assert.Equal(new SandboxOptions().DockerEndpoint, bound.DockerEndpoint);
    }

    [Fact]
    public void Read_ASettingsFile_ReachesTheOptions()
    {
        var folder = new DirectoryInfo(TestFolder.Create());

        try
        {
            var file = Written(folder, """{ "Image": "registry/sandbox-python:7", "AllowedHosts": ["objects.example"], "MaxConcurrentRuns": 2 }""");
            var bound = new SandboxOptions();

            SandboxSettings.Read(file, new Hashtable()).Bind(bound);

            Assert.Equal("registry/sandbox-python:7", bound.Image);
            Assert.Equal(["objects.example"], bound.AllowedHosts);
            Assert.Equal(2, bound.MaxConcurrentRuns);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>A variable is what a deployment says for this one run, so it overrules the file a person keeps.</summary>
    [Fact]
    public void Read_AVariableAndTheSettingsFile_TheVariableWins()
    {
        var folder = new DirectoryInfo(TestFolder.Create());

        try
        {
            var file = Written(folder, """{ "Image": "registry/sandbox-python:7", "MaxConcurrentRuns": 2 }""");
            var variables = new Hashtable { [Variable(nameof(SandboxOptions.Image), isList: false)] = "registry/sandbox-python:8" };
            var bound = new SandboxOptions();

            SandboxSettings.Read(file, variables).Bind(bound);

            Assert.Equal("registry/sandbox-python:8", bound.Image);
            Assert.Equal(2, bound.MaxConcurrentRuns);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static string Written(DirectoryInfo folder, string json)
    {
        var file = Path.Combine(folder.FullName, "config.json");
        File.WriteAllText(file, json);

        return file;
    }

    private static string Variable(string name, bool isList)
    {
        var words = string.Concat(name.Select((letter, index) => index > 0 && char.IsUpper(letter) ? $"_{letter}" : $"{letter}"));

        return $"{SandboxSettings.Prefix}{words.ToUpperInvariant()}{(isList ? "__0" : string.Empty)}";
    }

    private static string Sample(Type type) =>
        type == typeof(int) || type == typeof(long) ? "7"
        : type == typeof(double) ? "0.5"
        : "sample";

    private static string Shown(object? value) =>
        value is string[] list ? string.Join(",", list) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
