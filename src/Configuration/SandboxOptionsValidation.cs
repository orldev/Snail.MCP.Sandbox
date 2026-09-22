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

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    [GeneratedRegex(@"^(?<uid>\d+):(?<gid>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericUser();
}
