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
            failures.Add("Snail:Runtime is empty: name the runtime explicitly, runsc on a sandbox node.");

        if (string.IsNullOrWhiteSpace(options.Image))
            failures.Add("Snail:Image is empty.");

        if (!Uri.TryCreate(options.DockerEndpoint, UriKind.Absolute, out _))
            failures.Add("Snail:DockerEndpoint must be an absolute address, such as unix:///var/run/docker.sock.");

        if (NumericUser().Match(options.User) is not { Success: true } user)
            failures.Add("Snail:User must be a numeric uid:gid, such as 65534:65534.");
        else if (user.Groups["uid"].Value.TrimStart('0') is "")
            failures.Add("Snail:User must not be root.");

        if (options.MemoryBytes <= 0 || options.Cpus <= 0 || options.Pids <= 0 || options.MaxConcurrentRuns <= 0)
            failures.Add("Snail:MemoryBytes, Snail:Cpus, Snail:Pids and Snail:MaxConcurrentRuns must all be positive: an unset limit is no limit.");

        if (options.OutputLimitBytes <= 0 || options.OutFolderBytes <= 0 || options.TmpFolderBytes <= 0 || options.MaxOutputFiles <= 0)
            failures.Add("Snail:OutputLimitBytes, Snail:OutFolderBytes, Snail:TmpFolderBytes and Snail:MaxOutputFiles must all be positive.");

        if (options.DefaultTimeoutSeconds <= 0 || options.MaxTimeoutSeconds < options.DefaultTimeoutSeconds)
            failures.Add("Snail:DefaultTimeoutSeconds must be positive and not above Snail:MaxTimeoutSeconds.");

        if (string.IsNullOrWhiteSpace(options.DotnetImage) || string.IsNullOrWhiteSpace(options.NodeImage) || string.IsNullOrWhiteSpace(options.CacheVolumePrefix))
            failures.Add("Snail:DotnetImage, Snail:NodeImage and Snail:CacheVolumePrefix must all be set.");

        if (string.IsNullOrWhiteSpace(options.RestoreNetwork) || SharedNetwork().IsMatch(options.RestoreNetwork))
            failures.Add("Snail:RestoreNetwork must name a network of its own, or none: host and container: modes share a namespace the run must not see.");

        if ((options.RestoreProxy is { Length: > 0 }) != (options.RestoreProxyPort > 0))
            failures.Add("Snail:RestoreProxy and Snail:RestoreProxyPort are set together: an address nobody listens on fails every restore, and a proxy nobody is told of watches nothing.");

        if (options.RestoreProxy is { Length: > 0 } proxy && (!Uri.TryCreate(proxy, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttp || address.UserInfo.Length > 0))
            failures.Add("Snail:RestoreProxy must be an http address without credentials, such as http://mcp-sandbox:3128: the run's own name goes into it.");

        if (options.RestoreProxyPort is < 0 or > 65535)
            failures.Add("Snail:RestoreProxyPort must be a port, or 0 to keep the proxy off.");

        if (options.TestMemoryBytes <= 0 || options.TestCpus <= 0 || options.TestPids <= 0 || options.TestTmpFolderBytes <= 0 || options.MaxConcurrentTestRuns <= 0)
            failures.Add("Snail:TestMemoryBytes, Snail:TestCpus, Snail:TestPids, Snail:TestTmpFolderBytes and Snail:MaxConcurrentTestRuns must all be positive.");

        if (options.DefaultTestTimeoutSeconds <= 0 || options.MaxTestTimeoutSeconds < options.DefaultTestTimeoutSeconds)
            failures.Add("Snail:DefaultTestTimeoutSeconds must be positive and not above Snail:MaxTestTimeoutSeconds.");

        if (options.MaxProjectBytes <= 0 || options.MaxProjectFiles <= 0)
            failures.Add("Snail:MaxProjectBytes and Snail:MaxProjectFiles must both be positive.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    [GeneratedRegex(@"^\s*(host|container:.*)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SharedNetwork();

    [GeneratedRegex(@"^(?<uid>\d+):(?<gid>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericUser();
}
