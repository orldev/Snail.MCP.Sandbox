using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Snail.MCP.Sandbox.Configuration;

/// <summary>Refuses to start on settings that would run code less confined than they claim.</summary>
public sealed partial class SandboxOptionsValidation : IValidateOptions<SandboxOptions>
{
    public ValidateOptionsResult Validate(string? name, SandboxOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Runtime))
            failures.Add("SNAIL_MCP_SANDBOX_RUNTIME is empty: name the runtime explicitly, runsc on a sandbox node.");

        if (string.IsNullOrWhiteSpace(options.Image))
            failures.Add("SNAIL_MCP_SANDBOX_IMAGE is empty.");

        if (!Uri.TryCreate(options.DockerEndpoint, UriKind.Absolute, out _))
            failures.Add("SNAIL_MCP_SANDBOX_DOCKER_ENDPOINT must be an absolute address, such as unix:///var/run/docker.sock.");

        if (NumericUser().Match(options.User) is not { Success: true } user)
            failures.Add("SNAIL_MCP_SANDBOX_USER must be a numeric uid:gid, such as 65534:65534.");
        else if (user.Groups["uid"].Value.TrimStart('0') is "")
            failures.Add("SNAIL_MCP_SANDBOX_USER must not be root.");

        if (options.MemoryBytes <= 0 || !IsALimit(options.Cpus) || options.Pids <= 0 || options.MaxConcurrentRuns <= 0)
            failures.Add("SNAIL_MCP_SANDBOX_MEMORY_BYTES, SNAIL_MCP_SANDBOX_CPUS, SNAIL_MCP_SANDBOX_PIDS and SNAIL_MCP_SANDBOX_MAX_CONCURRENT_RUNS must all be positive numbers: an unset limit is no limit.");

        if (options.OutputLimitBytes <= 0 || options.OutFolderBytes <= 0 || options.TmpFolderBytes <= 0 || options.MaxOutputFiles <= 0)
            failures.Add("SNAIL_MCP_SANDBOX_OUTPUT_LIMIT_BYTES, SNAIL_MCP_SANDBOX_OUT_FOLDER_BYTES, SNAIL_MCP_SANDBOX_TMP_FOLDER_BYTES and SNAIL_MCP_SANDBOX_MAX_OUTPUT_FILES must all be positive.");

        if (options.DefaultTimeoutSeconds <= 0 || options.MaxTimeoutSeconds < options.DefaultTimeoutSeconds)
            failures.Add("SNAIL_MCP_SANDBOX_DEFAULT_TIMEOUT_SECONDS must be positive and not above SNAIL_MCP_SANDBOX_MAX_TIMEOUT_SECONDS.");

        if (string.IsNullOrWhiteSpace(options.DotnetImage) || string.IsNullOrWhiteSpace(options.NodeImage) || string.IsNullOrWhiteSpace(options.CacheVolumePrefix))
            failures.Add("SNAIL_MCP_SANDBOX_DOTNET_IMAGE, SNAIL_MCP_SANDBOX_NODE_IMAGE and SNAIL_MCP_SANDBOX_CACHE_VOLUME_PREFIX must all be set.");

        if (string.IsNullOrWhiteSpace(options.RestoreNetwork) || SharedNetwork().IsMatch(options.RestoreNetwork))
            failures.Add("SNAIL_MCP_SANDBOX_RESTORE_NETWORK must name a network of its own, or none: host and container: modes share a namespace the run must not see.");

        if ((options.RestoreProxy is { Length: > 0 }) != (options.RestoreProxyPort > 0))
            failures.Add("SNAIL_MCP_SANDBOX_RESTORE_PROXY and SNAIL_MCP_SANDBOX_RESTORE_PROXY_PORT are set together: an address nobody listens on fails every restore, and a proxy nobody is told of watches nothing.");

        if (options.RestoreProxy is { Length: > 0 } proxy && (!Uri.TryCreate(proxy, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttp || address.UserInfo.Length > 0))
            failures.Add("SNAIL_MCP_SANDBOX_RESTORE_PROXY must be an http address without credentials, such as http://mcp-sandbox:3128: the run's own name goes into it.");

        if (options.RestoreProxyPort is < 0 or > 65535)
            failures.Add("SNAIL_MCP_SANDBOX_RESTORE_PROXY_PORT must be a port, or 0 to keep the proxy off.");

        if (options.TestMemoryBytes <= 0 || !IsALimit(options.TestCpus) || options.TestPids <= 0 || options.TestTmpFolderBytes <= 0 || options.MaxConcurrentTestRuns <= 0)
            failures.Add("SNAIL_MCP_SANDBOX_TEST_MEMORY_BYTES, SNAIL_MCP_SANDBOX_TEST_CPUS, SNAIL_MCP_SANDBOX_TEST_PIDS, SNAIL_MCP_SANDBOX_TEST_TMP_FOLDER_BYTES and SNAIL_MCP_SANDBOX_MAX_CONCURRENT_TEST_RUNS must all be positive numbers.");

        if (options.DefaultTestTimeoutSeconds <= 0 || options.MaxTestTimeoutSeconds < options.DefaultTestTimeoutSeconds)
            failures.Add("SNAIL_MCP_SANDBOX_DEFAULT_TEST_TIMEOUT_SECONDS must be positive and not above SNAIL_MCP_SANDBOX_MAX_TEST_TIMEOUT_SECONDS.");

        if (options.MaxProjectBytes <= 0 || options.MaxProjectFiles <= 0)
            failures.Add("SNAIL_MCP_SANDBOX_MAX_PROJECT_BYTES and SNAIL_MCP_SANDBOX_MAX_PROJECT_FILES must both be positive.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

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
