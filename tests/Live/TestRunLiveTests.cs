using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Docker.DotNet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Live;

/// <summary>Real projects built and tested in a real container, the answer read as the model reads it.</summary>
/// <remarks>
/// Runs under whichever runtime <c>SNAIL_SANDBOX_LIVE_RUNTIME</c> names. The .NET projects restore xunit from nuget.org,
/// so they are given the default bridge network for the restore stage — and a Python test proves the network is gone
/// by the time the tests run. The images are the defaults of <see cref="SandboxOptions"/>; pull and build them first.
/// </remarks>
[Collection(LiveFactAttribute.Daemon)]
public sealed class TestRunLiveTests : IDisposable
{
    private static readonly string Runtime = Environment.GetEnvironmentVariable(LiveFactAttribute.RuntimeVariable) ?? string.Empty;

    private readonly string _folder = TestFolder.Create();

    private readonly DockerClient _docker = new DockerClientBuilder().WithEndpoint(new Uri(new SandboxOptions().DockerEndpoint)).WithTimeout(TimeSpan.FromMinutes(20)).Build();

    public void Dispose()
    {
        _docker.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [LiveFact]
    public async Task RunTests_DotnetProjectWithAFailingTest_CountsBothAndNamesTheFailure()
    {
        var report = await RunAsync("bridge", Dotnet(failing: true));

        Assert.Equal(TestOutcome.Failed, report.Outcome);
        Assert.Equal(2, report.Counts!.Total);
        Assert.Equal(1, report.Counts.Failed);
        Assert.Contains("Divides", Assert.Single(report.Counts.Failures).Name, StringComparison.Ordinal);
    }

    [LiveFact]
    public async Task RunTests_DotnetProjectThatDoesNotCompile_IsBuildFailedWithTheCompilerError()
    {
        var report = await RunAsync("bridge", Dotnet(failing: false, broken: true));

        Assert.Equal(TestOutcome.BuildFailed, report.Outcome);
        Assert.Contains("CS", report.Output, StringComparison.Ordinal);
    }

    /// <summary>Restore offline from an empty package volume cannot find xunit: the answer says restore, not build.</summary>
    [LiveFact]
    public async Task RunTests_DotnetRestoreWithoutANetworkOrCache_IsRestoreFailed()
    {
        var settings = Settings("none");
        settings.CacheVolumePrefix = $"snail-sandbox-live-empty-{Guid.NewGuid():N}";

        var report = await RunAsync(settings, Dotnet(failing: false));
        await _docker.Volumes.RemoveAsync($"{settings.CacheVolumePrefix}-dotnet", force: true);

        Assert.Equal(TestOutcome.RestoreFailed, report.Outcome);
    }

    /// <summary>A solution that lists no project builds nothing with exit code 0; the answer is a failed build that names why.</summary>
    [LiveFact]
    public async Task RunTests_SolutionListingNoProject_IsBuildFailedNamingTheRestoreWarning()
    {
        var report = await RunAsync("bridge", [.. Dotnet(failing: false).Select(entry => Moved(entry, "Tests/")), File("Calc.sln", EmptySolution)]);

        Assert.Equal(TestOutcome.BuildFailed, report.Outcome);
        Assert.Contains(Toolchains.EmptySolution, report.Output, StringComparison.Ordinal);
    }

    /// <summary>A repository whose global.json names Microsoft.Testing.Platform runs dotnet test in that mode and still gets counted.</summary>
    [LiveFact]
    public async Task RunTests_DotnetProjectOnTheTestingPlatform_CountsFromItsReport()
    {
        var report = await RunAsync("bridge",
            File("global.json", """{ "test": { "runner": "Microsoft.Testing.Platform" } }"""),
            File("Calc.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><IsPackable>false</IsPackable></PropertyGroup>
                  <ItemGroup><PackageReference Include="xunit.v3" Version="4.0.1" /></ItemGroup>
                </Project>
                """),
            File("CalcTests.cs", """
                public class CalcTests
                {
                    [Xunit.Fact] public void Adds() => Xunit.Assert.Equal(2, 1 + 1);
                    [Xunit.Fact] public void Divides() => Xunit.Assert.Equal(0.5, 1 / 2);
                }
                """));

        Assert.Equal(TestOutcome.Failed, report.Outcome);
        Assert.Equal(2, report.Counts!.Total);
        Assert.Contains("Divides", Assert.Single(report.Counts.Failures).Name, StringComparison.Ordinal);
    }

    /// <summary>A Python project that declares its dependencies only in pyproject.toml gets them installed before the tests.</summary>
    [LiveFact]
    public async Task RunTests_PythonProjectWithPyprojectOnly_InstallsItsDependencies()
    {
        var report = await RunAsync("bridge",
            File("pyproject.toml", "[project]\nname = \"calc\"\nversion = \"0.1.0\"\ndependencies = [\"six\"]\n\n[tool.setuptools]\npy-modules = [\"calc\"]\n"),
            File("calc.py", "import six\n\ndef text(value):\n    return six.text_type(value)\n"),
            File("test_calc.py", "from calc import text\n\ndef test_text():\n    assert text(1) == '1'\n"));

        Assert.Equal(TestOutcome.Passed, report.Outcome);
    }

    [LiveFact]
    public async Task RunTests_PythonProject_CountsFromPytestsReport()
    {
        var report = await RunAsync("none",
            File("calc.py", "def divide(a, b):\n    return a // b\n"),
            File("test_calc.py", "from calc import divide\n\ndef test_whole():\n    assert divide(4, 2) == 2\n\ndef test_fraction():\n    assert divide(1, 2) == 0.5\n"));

        Assert.Equal(TestOutcome.Failed, report.Outcome);
        Assert.Equal(2, report.Counts!.Total);
        Assert.Equal("test_calc.test_fraction", Assert.Single(report.Counts.Failures).Name);
    }

    /// <summary>The restore had a network; the tests, which run project code, must not.</summary>
    [LiveFact]
    public async Task RunTests_AfterARestoreWithANetwork_TheTestsRunOffline()
    {
        var report = await RunAsync("bridge",
            File("test_offline.py", "import socket, pytest\n\ndef test_no_network():\n    with pytest.raises(OSError):\n        socket.create_connection(('1.1.1.1', 53), timeout=3)\n"));

        Assert.Equal(TestOutcome.Passed, report.Outcome);
    }

    [LiveFact]
    public async Task RunTests_PythonWithoutTests_IsNoTests()
    {
        var report = await RunAsync("none", File("app.py", "print('hello')\n"));

        Assert.Equal(TestOutcome.NoTests, report.Outcome);
    }

    [LiveFact]
    public async Task RunTests_NodePackageWithNodesOwnRunner_Passes()
    {
        var report = await RunAsync("none",
            File("package.json", """{ "name": "calc", "version": "1.0.0", "scripts": { "test": "node --test" } }"""),
            File("calc.test.js", "const test = require('node:test');\nconst assert = require('node:assert');\ntest('adds', () => assert.strictEqual(1 + 1, 2));\n"));

        Assert.Equal(TestOutcome.Passed, report.Outcome);
    }

    private Task<TestReport> RunAsync(string restoreNetwork, params TarEntry[] files) => RunAsync(Settings(restoreNetwork), files);

    private SandboxOptions Settings(string restoreNetwork) => new() { Runtime = Runtime, WorkRoot = _folder, RestoreNetwork = restoreNetwork };

    private async Task<TestReport> RunAsync(SandboxOptions options, TarEntry[] files)
    {
        var archive = Path.Combine(_folder, $"{Guid.NewGuid():N}.tar.gz");
        await using (var file = System.IO.File.Create(archive))
        await using (var zipped = new GZipStream(file, CompressionLevel.Fastest))
        await using (var writer = new TarWriter(zipped, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in files)
                await writer.WriteEntryAsync(entry);
        }

        var runs = new TestRuns(
            new LocalInputs(),
            new TarProjectArchives(Options.Create(options)),
            new DockerTestRuns(_docker, Options.Create(options), NullLogger<DockerTestRuns>.Instance),
            Options.Create(options));

        var answer = await runs.RunAsync(new Uri(archive).AbsoluteUri, null, 900, CancellationToken.None);

        Assert.True(answer.IsSuccess, $"{answer.Error} {answer.Hint}");

        return answer.Value!;
    }

    private static TarEntry[] Dotnet(bool failing, bool broken = false) =>
    [
        File("Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """),
        File("CalcTests.cs", $$"""
            public class CalcTests
            {
                [Xunit.Fact] public void Adds() => Xunit.Assert.Equal(2, 1 + 1);
                [Xunit.Fact] public void Divides() => Xunit.Assert.Equal({{(failing ? "0.5" : "0")}}, 1 / 2){{(broken ? string.Empty : ";")}}
            }
            """)
    ];

    /// <summary>A solution file with no project in it, the way <c>dotnet new sln</c> leaves one.</summary>
    private const string EmptySolution = """

        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Global
        EndGlobal
        """;

    private static TarEntry Moved(TarEntry entry, string folder) =>
        new PaxTarEntry(TarEntryType.RegularFile, $"{folder}{entry.Name}") { DataStream = entry.DataStream, Mode = entry.Mode };

    private static PaxTarEntry File(string name, string text) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)), Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite };

    /// <summary>Hands the run a file already on this machine, as the object store would hand it a download.</summary>
    private sealed class LocalInputs : IInputs
    {
        public async Task<Answer<IReadOnlyList<InputFile>>> FetchAsync(IReadOnlyList<Uri> links, string folder, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, Path.GetFileName(links[0].LocalPath));

            await using (var source = System.IO.File.OpenRead(links[0].LocalPath))
            await using (var copy = System.IO.File.Create(target))
                await source.CopyToAsync(copy, cancellationToken);

            return Answer.Ok<IReadOnlyList<InputFile>>([new InputFile(Path.GetFileName(target), target, new FileInfo(target).Length)]);
        }

        public void Discard(string folder)
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
