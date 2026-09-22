namespace Snail.MCP.Sandbox.Tests.Live;

/// <summary>A test that runs only against a real Docker daemon, and only when the runtime to check is named.</summary>
/// <remarks>
/// Opt-in rather than automatic: a machine with Docker but without runsc would otherwise pass these checks under
/// runc and look like proof of an isolation it never tested. Naming the runtime makes the claim explicit.
/// </remarks>
public sealed class LiveFactAttribute : FactAttribute
{
    public const string RuntimeVariable = "SNAIL_SANDBOX_LIVE_RUNTIME";

    public LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RuntimeVariable)))
            Skip = $"Set {RuntimeVariable} to runsc on a sandbox node, or to runc to check the rest of the confinement locally.";
    }
}
