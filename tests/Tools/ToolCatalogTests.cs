using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Tools;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>
/// Guard tests over what the model reads: a number in a description is a promise about the code, and it drifts
/// from the code unless something fails when it does.
/// </summary>
public sealed class ToolCatalogTests
{
    private const long Megabyte = 1024 * 1024;

    private static readonly SandboxOptions Defaults = new();

    /// <summary>Every tool of the server, found the way the index finds them, so a new tool is guarded the day it appears.</summary>
    private static IEnumerable<MethodInfo> Tools =>
        typeof(RunTools).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);

    /// <summary>The tools the whole fleet shares carry the same name everywhere; prefixing one would hide that.</summary>
    private static readonly HashSet<string> Shared = new(StringComparer.Ordinal) { "find_tool" };

    private static string Contract =>
        string.Join('\n', ToolDescriptions.RunPython, ToolDescriptions.RunPythonCode, ToolDescriptions.RunPythonFiles, ToolDescriptions.RunPythonTimeout);

    /// <summary>What this server does carries its name; what every server of the fleet does carries the fleet's.</summary>
    [Fact]
    public void Tools_Each_AreNamedForWhoseTheyAreAndDescribed()
    {
        var tools = Tools.ToList();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool =>
        {
            var name = tool.GetCustomAttribute<McpServerToolAttribute>()!.Name!;

            Assert.True(name.StartsWith("sandbox_", StringComparison.Ordinal) || Shared.Contains(name), name);
            Assert.False(string.IsNullOrWhiteSpace(tool.GetCustomAttribute<DescriptionAttribute>()?.Description));
        });
    }

    /// <summary>A parameter the model cannot see the meaning of is one it fills in by guessing.</summary>
    [Fact]
    public void Parameters_AllButTheCancellationToken_AreDescribed() =>
        Assert.All(
            Tools.SelectMany(tool => tool.GetParameters()).Where(parameter => parameter.ParameterType != typeof(CancellationToken)),
            parameter => Assert.False(string.IsNullOrWhiteSpace(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description), parameter.Name));

    /// <summary>A run happens in a container that is gone when the call returns, and a diagnosis only asks the daemon.</summary>
    [Fact]
    public void Hints_EveryTool_IsReadOnlyAndClosedToTheWorld() =>
        Assert.All(Tools.Select(tool => tool.GetCustomAttribute<McpServerToolAttribute>()!), tool =>
        {
            Assert.True(tool.ReadOnly, tool.Name);
            Assert.False(tool.OpenWorld, tool.Name);
        });

    /// <summary>Defaults 536870912 bytes and 1.0 CPU: the description says 512 MB and 1 CPU.</summary>
    [Fact]
    public void Description_ResourceLimits_MatchTheDefaults()
    {
        Assert.Equal(536870912, Defaults.MemoryBytes);
        Assert.Contains($"{Defaults.MemoryBytes / Megabyte} MB of memory", Contract, StringComparison.Ordinal);
        Assert.Contains($"{Defaults.Cpus} CPU", Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_Timeouts_MatchTheDefaults()
    {
        Assert.Contains($"from 1 to {Defaults.MaxTimeoutSeconds}", ToolDescriptions.RunPythonTimeout, StringComparison.Ordinal);
        Assert.Contains($"{Defaults.DefaultTimeoutSeconds} when omitted", ToolDescriptions.RunPythonTimeout, StringComparison.Ordinal);
    }

    /// <summary>Default 65536 bytes a stream: the description says 64 KB each.</summary>
    [Fact]
    public void Description_OutputLimit_MatchesTheDefault()
    {
        Assert.Equal(65536, Defaults.OutputLimitBytes);
        Assert.Contains($"{Defaults.OutputLimitBytes / 1024} KB each", Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_OutFolder_NamesItsPathAndLimits()
    {
        Assert.Contains($"Write charts, tables and reports to {Confinement.OutFolder}", Contract, StringComparison.Ordinal);
        Assert.Contains($"up to {Defaults.MaxOutputFiles} files, {Defaults.OutFolderBytes / Megabyte} MB in all", Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_InputLimits_MatchTheDefaults()
    {
        Assert.Contains($"at most {Defaults.MaxInputFiles}", ToolDescriptions.RunPythonFiles, StringComparison.Ordinal);
        Assert.Contains($"each up to {Defaults.MaxInputBytes / Megabyte} MB", ToolDescriptions.RunPythonFiles, StringComparison.Ordinal);
        Assert.Contains($"{Defaults.MaxInputTotalBytes / Megabyte} MB in all", ToolDescriptions.RunPythonFiles, StringComparison.Ordinal);
    }

    /// <summary>The server's instructions name the tool; renaming it without them leaves the model looking for a tool that is gone.</summary>
    [Fact]
    public void Guidance_NamesEveryTool() =>
        Assert.All(Tools, tool => Assert.Contains(tool.GetCustomAttribute<McpServerToolAttribute>()!.Name!, ServerGuidance.Instructions, StringComparison.Ordinal));
}
