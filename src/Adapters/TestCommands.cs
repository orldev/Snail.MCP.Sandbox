using System.Globalization;
using Docker.DotNet.Models;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>The container a test run gets and the command of each of its stages, per toolchain.</summary>
/// <remarks>
/// The confinement is the run's: no capability, no new privilege, a read-only root, the run user, limits on memory,
/// CPU and processes. Two things differ, both because a build is not a script. The project folder is the run user's
/// and writable, since a build writes beside its sources. And the container starts on <see cref="SandboxOptions.RestoreNetwork"/>
/// so the restore stage can fetch packages; the network is taken away before the build, and the build and the tests
/// never have one. Packages land in a volume per toolchain that outlives the run, so a second run restores offline.
/// </remarks>
internal static class TestCommands
{
    public const string ProjectPath = $"{Confinement.Root}/{ProjectArchive.Folder}";

    public const string DependenciesPath = $"{Confinement.Root}/{ProjectArchive.DependenciesFolder}";

    public const string ResultsPath = $"{Confinement.OutFolder}/results";

    public const string CachePath = "/cache";

    /// <summary>The stages of a toolchain, in the order they run.</summary>
    public static IReadOnlyList<Stage> Stages(Toolchain toolchain) => toolchain switch
    {
        Toolchain.Python => [Stage.Restore, Stage.Test],
        _ => [Stage.Restore, Stage.Build, Stage.Test]
    };

    public static string Image(Toolchain toolchain, SandboxOptions options) => toolchain switch
    {
        Toolchain.Dotnet => options.DotnetImage,
        Toolchain.Node => options.NodeImage,
        _ => options.Image
    };

    /// <summary>The volume a toolchain keeps its downloaded packages in, shared by every run of it on the node.</summary>
    public static string CacheVolume(Toolchain toolchain, SandboxOptions options) =>
        $"{options.CacheVolumePrefix}-{toolchain.ToString().ToLowerInvariant()}";

    public static IList<string> Command(Toolchain toolchain, Stage stage, IReadOnlyList<string> paths) => toolchain switch
    {
        Toolchain.Dotnet => Dotnet(stage, Toolchains.DotnetTarget(paths)),
        Toolchain.Node => Node(stage),
        _ => Python(stage)
    };

    public static IList<string> Environment(Toolchain toolchain) => toolchain switch
    {
        Toolchain.Dotnet =>
        [
            "HOME=/tmp", "DOTNET_CLI_HOME=/tmp", $"NUGET_PACKAGES={CachePath}/nuget", "DOTNET_NOLOGO=1",
            "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1", "DOTNET_CLI_UI_LANGUAGE=en",
            "MSBUILDDISABLENODEREUSE=1", "DOTNET_GENERATE_ASPNET_CERTIFICATE=false"
        ],
        Toolchain.Node => ["HOME=/tmp", $"npm_config_cache={CachePath}/npm", "npm_config_update_notifier=false", "npm_config_fund=false", "npm_config_audit=false"],
        _ =>
        [
            "HOME=/tmp", $"PIP_CACHE_DIR={CachePath}/pip", "PIP_DISABLE_PIP_VERSION_CHECK=1", "PIP_NO_INPUT=1",
            $"PYTHONPATH={DependenciesPath}:{ProjectPath}", "PYTHONDONTWRITEBYTECODE=1", "PYTHONUNBUFFERED=1", "MPLBACKEND=Agg"
        ]
    };

    /// <summary>The variables pip, npm and the .NET SDK take a proxy from; only the restore is given them.</summary>
    public static IList<string> Proxied(string proxy) =>
        [$"HTTPS_PROXY={proxy}", $"HTTP_PROXY={proxy}", $"https_proxy={proxy}", $"http_proxy={proxy}", $"npm_config_https_proxy={proxy}", $"npm_config_proxy={proxy}"];

    public static CreateContainerParameters Container(Toolchain toolchain, SandboxOptions options, TimeSpan timeout) => new()
    {
        Image = Image(toolchain, options),
        User = options.User,
        Hostname = "sandbox",
        Cmd = ["sleep", Math.Ceiling((timeout + Confinement.Grace).TotalSeconds).ToString(CultureInfo.InvariantCulture)],
        Labels = new Dictionary<string, string> { [Confinement.Label] = "test" },
        HostConfig = new HostConfig
        {
            Runtime = options.Runtime,
            NetworkMode = options.RestoreNetwork,
            ReadonlyRootfs = true,
            CapDrop = ["ALL"],
            SecurityOpt = ["no-new-privileges"],
            Memory = options.TestMemoryBytes,
            MemorySwap = options.TestMemoryBytes,
            NanoCPUs = (long)(options.TestCpus * 1_000_000_000),
            PidsLimit = options.TestPids,
            Ulimits = Confinement.Processes(options.Runtime, options.TestPids),
            AutoRemove = true,
            LogConfig = new LogConfig { Type = "none", Config = new Dictionary<string, string>() },
            Tmpfs = new Dictionary<string, string>
            {
                ["/tmp"] = $"rw,nosuid,nodev,exec,size={options.TestTmpFolderBytes}",
                [Confinement.OutFolder] = Confinement.Outputs(options)
            },
            Mounts =
            [
                new Mount { Type = "volume", Target = Confinement.Root, VolumeOptions = new VolumeOptions { NoCopy = true } },
                new Mount { Type = "volume", Source = CacheVolume(toolchain, options), Target = CachePath }
            ]
        }
    };

    public static ContainerExecCreateParameters Execution(Toolchain toolchain, Stage stage, IReadOnlyList<string> paths, SandboxOptions options, string? proxy = null) => new()
    {
        User = options.User,
        WorkingDir = ProjectPath,
        Cmd = Command(toolchain, stage, paths),
        Env = stage == Stage.Restore && proxy is not null ? [.. Environment(toolchain), .. Proxied(proxy)] : Environment(toolchain),
        AttachStdout = true,
        AttachStderr = true
    };

    /// <summary>The one-off container that hands a new package volume to the run user, run as root with only the right to change owners.</summary>
    /// <remarks>
    /// A fresh volume is root's, and the run user could not write a package into it. This container runs no code of
    /// the project: its command is fixed here, it has no network, and CHOWN is the only capability it holds.
    /// </remarks>
    public static CreateContainerParameters Handover(Toolchain toolchain, SandboxOptions options) => new()
    {
        Image = Image(toolchain, options),
        User = "0:0",
        Cmd = ["chown", options.User, CachePath],
        Labels = new Dictionary<string, string> { [Confinement.Label] = "handover" },
        HostConfig = new HostConfig
        {
            Runtime = options.Runtime,
            NetworkMode = "none",
            ReadonlyRootfs = true,
            CapDrop = ["ALL"],
            CapAdd = ["CHOWN"],
            SecurityOpt = ["no-new-privileges"],
            LogConfig = new LogConfig { Type = "none", Config = new Dictionary<string, string>() },
            Mounts = [new Mount { Type = "volume", Source = CacheVolume(toolchain, options), Target = CachePath }]
        }
    };

    private static IList<string> Dotnet(Stage stage, string? target)
    {
        List<string> aimed = target is null ? [] : [target];

        return stage switch
        {
            Stage.Restore => ["dotnet", "restore", .. aimed, "--disable-build-servers"],
            Stage.Build => ["dotnet", "build", .. aimed, "--no-restore", "--disable-build-servers", "-nologo", "-v", "q"],
            _ => ["sh", "-c", DotnetTest(target)]
        };
    }

    /// <summary>The test stage of a .NET project, in whichever of the two modes its global.json puts <c>dotnet test</c>.</summary>
    /// <remarks>
    /// A repository whose global.json names Microsoft.Testing.Platform as the test runner puts <c>dotnet test</c> in that
    /// platform's mode, where <c>--logger trx</c> is refused, the report is asked for with <c>--report-trx</c> and the
    /// solution or project is named by an option rather than by position. Read inside the container, from the project as
    /// it came, because the command is built from the file names alone. <c>--report-trx</c> belongs to the platform's TRX
    /// extension, which MSTest carries and xunit.v3 does not — xunit.v3 writes the same report by its own
    /// <c>--report-xunit-trx</c> — and the platform answers an option nobody offers with exit code 5 before any test runs,
    /// so that is when the run is asked again the other way.
    /// </remarks>
    public static string DotnetTest(string? target)
    {
        string vstest = target is null ? string.Empty : $"{Quoted(target)} ";
        string platform = target switch
        {
            null => string.Empty,
            _ when target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) => $"--solution {Quoted(target)} ",
            _ => $"--project {Quoted(target)} "
        };

        return $"if grep -qs 'Microsoft.Testing.Platform' global.json; "
            + $"then dotnet test {platform}--no-build --report-trx --results-directory {ResultsPath}; code=$?; "
            + $"if [ $code = {Toolchains.UnknownOption} ]; then exec dotnet test {platform}--no-build --report-xunit-trx --results-directory {ResultsPath}; fi; exit $code; "
            + $"else exec dotnet test {vstest}--no-build --logger trx --results-directory {ResultsPath}; fi";
    }

    /// <summary>A file name as one word of a shell line, whatever quotes it carries.</summary>
    private static string Quoted(string word) => $"'{word.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static IList<string> Python(Stage stage) => stage switch
    {
        Stage.Restore =>
        [
            "sh", "-c",
            $"found=0; for wanted in requirements.txt requirements-dev.txt requirements-test.txt; do if [ -f \"$wanted\" ]; then found=1; python -m pip install --target {DependenciesPath} -r \"$wanted\" || exit $?; fi; done; "
            + $"if [ $found = 0 ] && [ -f pyproject.toml ]; then python -m pip install --target {DependenciesPath} '.[test,dev]' || exit $?; fi"
        ],
        _ => ["python", "-m", "pytest", "-q", "-p", "no:cacheprovider", $"--junitxml={ResultsPath}/junit.xml"]
    };

    private static IList<string> Node(Stage stage) => stage switch
    {
        Stage.Restore => ["sh", "-c", "if [ -f package-lock.json ]; then npm ci; else npm install; fi"],
        Stage.Build => ["npm", "run", "build", "--if-present"],
        _ =>
        [
            "sh", "-c",
            $"node -e \"const test = (require('./package.json').scripts || {{}}).test; process.exit(test && !/no test specified/.test(test) ? 0 : {Toolchains.NoTestScript})\" && npm test"
        ]
    };
}
