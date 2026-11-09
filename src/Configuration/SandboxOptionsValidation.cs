using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Sandbox.Configuration;

/// <summary>Refuses to start on settings that would run code less confined than they claim.</summary>
public sealed partial class SandboxOptionsValidation : IValidateOptions<SandboxOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SandboxOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Runtime))
            failures.Add($"{Setting(nameof(SandboxOptions.Runtime))} is empty: name the runtime explicitly, runsc on a sandbox node.");

        if (string.IsNullOrWhiteSpace(options.Image))
            failures.Add($"{Setting(nameof(SandboxOptions.Image))} is empty.");

        if (!Uri.TryCreate(options.DockerEndpoint, UriKind.Absolute, out _))
            failures.Add($"{Setting(nameof(SandboxOptions.DockerEndpoint))} must be an absolute address, such as unix:///var/run/docker.sock.");

        if (NumericUser().Match(options.User) is not { Success: true } user)
            failures.Add($"{Setting(nameof(SandboxOptions.User))} must be a numeric uid:gid, such as 65534:65534.");
        else if (user.Groups["uid"].Value.TrimStart('0') is "")
            failures.Add($"{Setting(nameof(SandboxOptions.User))} must not be root.");

        if (options.MemoryBytes <= 0 || !IsALimit(options.Cpus) || options.Pids <= 0 || options.MaxConcurrentRuns <= 0)
            failures.Add($"{Setting(nameof(SandboxOptions.MemoryBytes))}, {Setting(nameof(SandboxOptions.Cpus))}, {Setting(nameof(SandboxOptions.Pids))} and {Setting(nameof(SandboxOptions.MaxConcurrentRuns))} must all be positive numbers: an unset limit is no limit.");

        if (options.MaxQueuedRuns < 0 || options.MaxQueuedTestRuns < 0)
            failures.Add($"{Setting(nameof(SandboxOptions.MaxQueuedRuns))} and {Setting(nameof(SandboxOptions.MaxQueuedTestRuns))} must not be negative; 0 refuses every call past the places.");

        if (options.OutputLimitBytes <= 0 || options.OutFolderBytes <= 0 || options.TmpFolderBytes <= 0 || options.MaxOutputFiles <= 0)
            failures.Add($"{Setting(nameof(SandboxOptions.OutputLimitBytes))}, {Setting(nameof(SandboxOptions.OutFolderBytes))}, {Setting(nameof(SandboxOptions.TmpFolderBytes))} and {Setting(nameof(SandboxOptions.MaxOutputFiles))} must all be positive.");

        if (options.DefaultTimeoutSeconds <= 0 || options.MaxTimeoutSeconds < options.DefaultTimeoutSeconds)
            failures.Add($"{Setting(nameof(SandboxOptions.DefaultTimeoutSeconds))} must be positive and not above {Setting(nameof(SandboxOptions.MaxTimeoutSeconds))}.");

        if (string.IsNullOrWhiteSpace(options.DotnetImage) || string.IsNullOrWhiteSpace(options.NodeImage) || string.IsNullOrWhiteSpace(options.CacheVolumePrefix))
            failures.Add($"{Setting(nameof(SandboxOptions.DotnetImage))}, {Setting(nameof(SandboxOptions.NodeImage))} and {Setting(nameof(SandboxOptions.CacheVolumePrefix))} must all be set.");

        if (string.IsNullOrWhiteSpace(options.RestoreNetwork) || SharedNetwork().IsMatch(options.RestoreNetwork))
            failures.Add($"{Setting(nameof(SandboxOptions.RestoreNetwork))} must name a network of its own, or none: host and container: modes share a namespace the run must not see.");

        if ((options.RestoreProxy is { Length: > 0 }) != (options.RestoreProxyPort > 0))
            failures.Add($"{Setting(nameof(SandboxOptions.RestoreProxy))} and {Setting(nameof(SandboxOptions.RestoreProxyPort))} are set together: an address nobody listens on fails every restore, and a proxy nobody is told of watches nothing.");

        if (options.RestoreProxy is { Length: > 0 } proxy && (!Uri.TryCreate(proxy, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttp || address.UserInfo.Length > 0))
            failures.Add($"{Setting(nameof(SandboxOptions.RestoreProxy))} must be an http address without credentials, such as http://mcp-sandbox:3128: the run's own name goes into it.");

        if (options.RestoreProxyPort is < 0 or > 65535)
            failures.Add($"{Setting(nameof(SandboxOptions.RestoreProxyPort))} must be a port, or 0 to keep the proxy off.");

        if (options.TestMemoryBytes <= 0 || !IsALimit(options.TestCpus) || options.TestPids <= 0 || options.TestTmpFolderBytes <= 0 || options.MaxConcurrentTestRuns <= 0)
            failures.Add($"{Setting(nameof(SandboxOptions.TestMemoryBytes))}, {Setting(nameof(SandboxOptions.TestCpus))}, {Setting(nameof(SandboxOptions.TestPids))}, {Setting(nameof(SandboxOptions.TestTmpFolderBytes))} and {Setting(nameof(SandboxOptions.MaxConcurrentTestRuns))} must all be positive numbers.");

        if (options.DefaultTestTimeoutSeconds <= 0 || options.MaxTestTimeoutSeconds < options.DefaultTestTimeoutSeconds)
            failures.Add($"{Setting(nameof(SandboxOptions.DefaultTestTimeoutSeconds))} must be positive and not above {Setting(nameof(SandboxOptions.MaxTestTimeoutSeconds))}.");

        if (options.MaxProjectBytes <= 0 || options.MaxProjectFiles <= 0)
            failures.Add($"{Setting(nameof(SandboxOptions.MaxProjectBytes))} and {Setting(nameof(SandboxOptions.MaxProjectFiles))} must both be positive.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>The setting as an operator writes it: its variable, and its key in the settings file.</summary>
    private static string Setting(string key) => ServerSettings.NameOfSetting(SandboxSettings.Name, key);

    /// <summary>Whether a CPU count limits anything: positive and finite.</summary>
    /// <remarks>
    /// The binder reads NaN and Infinity as numbers, and neither is below zero. Measured on .NET 10: NaN cast to
    /// nanoseconds of CPU is 0, which the daemon takes as no limit at all, so the run would be unconfined under a
    /// setting that claims otherwise. Infinity saturates to a value the daemon refuses, which at least fails loudly.
    /// </remarks>
    private static bool IsALimit(double cpus) => double.IsFinite(cpus) && cpus > 0;

    [GeneratedRegex(@"^\s*(host|container:.*)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SharedNetwork();

    [GeneratedRegex(@"^(?<uid>\d+):(?<gid>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericUser();
}
