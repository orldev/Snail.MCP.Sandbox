using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Tools;
using Snail.MCP.Sandbox.Domain;

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
    /// <remarks>The test run alone is open to the world: its restore stage fetches packages over the network it is given.</remarks>
    [Fact]
    public void Hints_EveryTool_IsReadOnlyAndOnlyTheTestRunIsOpenToTheWorld() =>
        Assert.All(Tools.Select(tool => tool.GetCustomAttribute<McpServerToolAttribute>()!), tool =>
        {
            Assert.True(tool.ReadOnly, tool.Name);
            Assert.Equal(tool.Name == "sandbox_run_tests", tool.OpenWorld);
        });

    /// <summary>Every outcome the answer can carry is named to the model, spelled the way the answer spells it.</summary>
    [Fact]
    public void Description_Outcomes_NameEveryTestOutcome()
    {
        var named = ToolDescriptions.Outcomes.Replace(" or ", ", ", StringComparison.Ordinal).Split(", ");

        Assert.Equal(
            Enum.GetValues<TestOutcome>().Select(outcome => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString())).Order(),
            named.Order());
        Assert.Contains(ToolDescriptions.Outcomes, ToolDescriptions.RunTests, StringComparison.Ordinal);
    }

    /// <summary>A field the model is not told about is one it reads past: diagnose names untestable as the answer carries it.</summary>
    [Fact]
    public void Description_Diagnose_NamesTheUntestableField()
    {
        var fields = ToolResponse.From(Answer.Ok(new Readiness("29.1.0", "runsc", "image"))).StructuredContent!.Value.EnumerateObject().Select(field => field.Name);

        Assert.Contains("untestable", fields);
        Assert.Contains("untestable", ToolDescriptions.Diagnose, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_Toolchains_NameEveryOneACallerMayGive() =>
        Assert.All(Toolchains.Names, name => Assert.Contains(name, ToolDescriptions.RunTestsToolchain, StringComparison.Ordinal));

    /// <summary>Defaults 2147483648 bytes, 2.0 CPU, 268435456 bytes and 20000 files: the description says 2048 MB, 2 CPU, 256 MB and 20000 files.</summary>
    [Fact]
    public void Description_TestRunLimits_MatchTheDefaults()
    {
        Assert.Contains($"{Defaults.TestMemoryBytes / Megabyte} MB of memory and {Defaults.TestCpus} CPU", ToolDescriptions.RunTests, StringComparison.Ordinal);
        Assert.Contains($"unpack to {Defaults.MaxProjectBytes / Megabyte} MB and {Defaults.MaxProjectFiles} files", ToolDescriptions.RunTests, StringComparison.Ordinal);
        Assert.Contains($"first {TestReports.MaxFailures} failures", ToolDescriptions.RunTests, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_TestRunTimeouts_MatchTheDefaults()
    {
        Assert.Contains($"from 1 to {Defaults.MaxTestTimeoutSeconds}", ToolDescriptions.RunTestsTimeout, StringComparison.Ordinal);
        Assert.Contains($"{Defaults.DefaultTestTimeoutSeconds} when omitted", ToolDescriptions.RunTestsTimeout, StringComparison.Ordinal);
    }

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

    /// <summary>The wait a job tool takes is read by the SDK's client under one name, and its default is a promise the description makes.</summary>
    [Fact]
    public void Parameters_Wait_IsTheJobClientsAndDefaultsAsDescribed()
    {
        var waits = Tools.SelectMany(tool => tool.GetParameters()).Where(parameter => parameter.Name == Snail.Sdk.Mcp.JobClient.JobClient.WaitParameter).ToList();

        Assert.Equal(2, waits.Count);
        Assert.All(waits, wait => Assert.Contains($"{wait.DefaultValue} when", ToolDescriptions.Wait, StringComparison.Ordinal));
    }

    /// <summary>The longest wait is the longest test run plus the grace its container outlives it by: a longer wait would outlive the work it waits for.</summary>
    [Fact]
    public void Parameters_LongestWait_IsTheLongestTestRunAndItsGrace()
    {
        Assert.Equal(ToolDescriptions.LongestWait, Defaults.MaxTestTimeoutSeconds + (int)Confinement.Grace.TotalSeconds);
        Assert.Contains($"from 0 to {ToolDescriptions.LongestWait}", ToolDescriptions.Wait, StringComparison.Ordinal);
    }

    /// <summary>The server's instructions name the tool; renaming it without them leaves the model looking for a tool that is gone.</summary>
    [Fact]
    public void Guidance_NamesEveryTool() =>
        Assert.All(Tools, tool => Assert.Contains(tool.GetCustomAttribute<McpServerToolAttribute>()!.Name!, ServerGuidance.Instructions, StringComparison.Ordinal));
}
