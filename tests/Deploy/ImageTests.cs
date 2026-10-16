using System.Text.RegularExpressions;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Tools;

namespace Snail.MCP.Sandbox.Tests.Deploy;

/// <summary>
/// Guard tests between the images and what the code and the model are told about them: a library named in the
/// description and missing from the image is an ImportError the model cannot fix, since nothing installs at run time.
/// </summary>
public sealed partial class ImageTests
{
    private static string Executor => File.ReadAllText(Path.Combine(Repository.Root, "deploy", "executor", "Dockerfile"));

    [Fact]
    public void Executor_NamedLibraries_AreExactlyTheOnesItInstallsByName()
    {
        var named = ToolDescriptions.Libraries.Split([", ", " and "], StringSplitOptions.RemoveEmptyEntries);
        var installed = File.ReadLines(Path.Combine(Repository.Root, "deploy", "executor", "requirements.txt"))
            .SkipWhile(line => line.StartsWith('#'))
            .TakeWhile(line => line.Length > 0)
            .Select(line => line.Split("==")[0]);

        Assert.Equal(named.Order(StringComparer.Ordinal), installed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Executor_PythonVersion_IsTheOneTheDescriptionNames()
    {
        var version = PythonBase().Match(Executor).Groups["version"].Value;

        Assert.NotEmpty(version);
        Assert.Contains($"Python {version} program", ToolDescriptions.RunPython, StringComparison.Ordinal);
    }

    /// <summary>The container is started as the configured user anyway; the image's own user matters when someone runs it by hand.</summary>
    [Fact]
    public void Executor_User_IsTheDefaultRunUser() =>
        Assert.Contains($"USER {new SandboxOptions().User}", Executor, StringComparison.Ordinal);

    /// <summary>The working directory a hand-run container opens in is the one a run's files are placed in.</summary>
    [Fact]
    public void Executor_WorkingDirectory_IsWhereARunFindsItsFiles() =>
        Assert.Contains($"WORKDIR {Confinement.Execution(new SandboxOptions()).WorkingDir}", Executor, StringComparison.Ordinal);

    [Fact]
    public void Workflow_EveryDockerfile_IsBuilt()
    {
        var workflow = File.ReadAllText(Path.Combine(Repository.Root, ".github", "workflows", "images.yml"));
        var dockerfiles = Directory.GetFiles(Path.Combine(Repository.Root, "deploy"), "Dockerfile", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Repository.Root, path).Replace('\\', '/'));

        Assert.All(dockerfiles, dockerfile => Assert.Contains($"dockerfile: {dockerfile}", workflow, StringComparison.Ordinal));
    }

    /// <summary>A default image the workflow never builds is a server that refuses every run on a fresh node.</summary>
    [Fact]
    public void Defaults_Image_IsTheExecutorTheWorkflowBuilds()
    {
        var workflow = File.ReadAllText(Path.Combine(Repository.Root, ".github", "workflows", "images.yml"));
        var name = new SandboxOptions().Image.Split(':')[0].Split('/')[^1];

        Assert.Contains($"dockerfile: deploy/executor/Dockerfile\n      image: {name}\n", workflow, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^FROM python:(?<version>\d+\.\d+)", RegexOptions.Multiline)]
    private static partial Regex PythonBase();
}
