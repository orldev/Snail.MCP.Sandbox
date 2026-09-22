using System.Globalization;
using System.Reflection;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Tests.Deploy;

/// <summary>The configuration table is what an operator sets a node up from; a setting missing there is one nobody sets.</summary>
public sealed class ReadmeTests
{
    private static readonly string Readme = File.ReadAllText(Path.Combine(Repository.Root, "README.md"));

    private static IEnumerable<PropertyInfo> Settings =>
        typeof(SandboxOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    [Fact]
    public void Configuration_EverySetting_HasARow() =>
        Assert.All(Settings, setting => Assert.Contains($"| `{setting.Name}` |", Readme, StringComparison.Ordinal));

    /// <summary>Numbers only: the rows for text and lists describe their default in words.</summary>
    [Fact]
    public void Configuration_NumericDefaults_AreTheOnesTheCodeHas()
    {
        var defaults = new SandboxOptions();
        var numeric = Settings.Where(setting => setting.PropertyType == typeof(int) || setting.PropertyType == typeof(long) || setting.PropertyType == typeof(double));

        Assert.All(numeric, setting =>
        {
            var value = setting.GetValue(defaults) is double number
                ? number.ToString("0.0##", CultureInfo.InvariantCulture)
                : Convert.ToString(setting.GetValue(defaults), CultureInfo.InvariantCulture);
            var row = Readme.Split('\n').Single(line => line.StartsWith($"| `{setting.Name}` |", StringComparison.Ordinal));

            Assert.EndsWith($"| `{value}` |", row.TrimEnd(), StringComparison.Ordinal);
        });
    }
}
