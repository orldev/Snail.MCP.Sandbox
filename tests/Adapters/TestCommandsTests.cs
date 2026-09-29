using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Adapters;

/// <summary>The commands a stage runs, read as the container's shell will read them.</summary>
public sealed class TestCommandsTests
{
    /// <summary>A global.json naming Microsoft.Testing.Platform puts dotnet test in its mode: the report by --report-trx, the solution by option.</summary>
    [Fact]
    public void DotnetTest_Solution_AsksForTheReportInEitherMode()
    {
        string line = TestCommands.DotnetTest("Calc.slnx");

        Assert.Contains("grep -qs 'Microsoft.Testing.Platform' global.json", line, StringComparison.Ordinal);
        Assert.Contains("dotnet test --solution 'Calc.slnx' --no-build --report-trx", line, StringComparison.Ordinal);
        Assert.Contains("dotnet test 'Calc.slnx' --no-build --logger trx", line, StringComparison.Ordinal);
    }

    /// <summary>xunit.v3 has no platform TRX extension: an unknown option (5) is asked again with its own report flag.</summary>
    [Fact]
    public void DotnetTest_TestingPlatformWithoutTheTrxExtension_AsksAgainTheXunitWay() =>
        Assert.Contains($"if [ $code = {Toolchains.UnknownOption} ]; then exec dotnet test --solution 'Calc.sln' --no-build --report-xunit-trx", TestCommands.DotnetTest("Calc.sln"), StringComparison.Ordinal);

    [Fact]
    public void DotnetTest_Project_IsNamedByProjectInTheTestingPlatformMode() =>
        Assert.Contains("--project 'Calc.Tests.csproj' ", TestCommands.DotnetTest("Calc.Tests.csproj"), StringComparison.Ordinal);

    [Fact]
    public void DotnetTest_NoTarget_LeavesDotnetToFindOne() =>
        Assert.Contains("dotnet test --no-build --logger trx", TestCommands.DotnetTest(null), StringComparison.Ordinal);

    /// <summary>Breaks the shell: a quote in a file name stays inside its word instead of ending the command.</summary>
    [Fact]
    public void DotnetTest_NameWithAQuote_StaysOneWord() =>
        Assert.Contains("'it'\\''s.sln'", TestCommands.DotnetTest("it's.sln"), StringComparison.Ordinal);

    /// <summary>A Python project that declares its dependencies in pyproject.toml alone gets them, with its test extras.</summary>
    [Fact]
    public void PythonRestore_ProjectWithoutRequirements_InstallsFromPyproject()
    {
        string line = TestCommands.Command(Toolchain.Python, Stage.Restore, ["pyproject.toml"])[2];

        Assert.Contains("if [ $found = 0 ] && [ -f pyproject.toml ]", line, StringComparison.Ordinal);
        Assert.Contains("'.[test,dev]'", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DotnetTest_Stage_RunsThroughTheShell() =>
        Assert.Equal(["sh", "-c", TestCommands.DotnetTest("Calc.sln")], TestCommands.Command(Toolchain.Dotnet, Stage.Test, ["Calc.sln"]));

    /// <summary>The reports come back through /out, which executes nothing under gVisor only as a tmpfs of the container itself.</summary>
    [Fact]
    public void Container_OutFolder_IsATmpfsThatExecutesNothing()
    {
        var host = TestCommands.Container(Toolchain.Dotnet, new SandboxOptions { User = "1000:1001" }, TimeSpan.FromSeconds(60)).HostConfig!;

        Assert.Contains("noexec", host.Tmpfs![Confinement.OutFolder].Split(','));
        Assert.DoesNotContain(host.Mounts!, mount => mount.Target == Confinement.OutFolder);
    }
}
