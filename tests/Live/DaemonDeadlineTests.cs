using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Tests.Adapters;

namespace Snail.MCP.Sandbox.Tests.Live;

/// <summary>The run adapter against a daemon that stops answering: the platform's probe calls the diagnosis, and a probe that hangs is a node that looks alive.</summary>
/// <remarks>
/// On the scripted daemon, not a real one, but each test waits out the whole deadline — five seconds — so they run with
/// the live set rather than lengthening every ordinary run.
/// </remarks>
public sealed class DaemonDeadlineTests
{
    /// <summary>
    /// Breaks the daemon: a diagnosis asked of a socket that never answers has no deadline of its own. The only bound is
    /// the Docker client's, the longest test run plus its grace — thirty-one minutes, which is a probe that never fails.
    /// </summary>
    [LiveFact]
    public async Task DiagnoseAsync_DaemonNeverAnswers_FailsWithinSeconds()
    {
        var daemon = new Daemon().Answer("GetSystemInfoAsync", _ => new TaskCompletionSource<SystemInfoResponse>().Task);
        var containers = new DockerContainers(daemon.Client, Options.Create(new SandboxOptions()), NullLogger<DockerContainers>.Instance);

        var diagnosis = containers.DiagnoseAsync(CancellationToken.None);
        var first = await Task.WhenAny(diagnosis, Task.Delay(DockerContainers.AnswerWait + TimeSpan.FromSeconds(3)));

        Assert.Same(diagnosis, first);
        Assert.Contains("did not answer within", (await diagnosis).Error, StringComparison.Ordinal);
    }

    /// <summary>Breaks the daemon under a run: the runtime check before any container exists waits the same few seconds, not the client's thirty-one minutes.</summary>
    [LiveFact]
    public async Task RunAsync_DaemonNeverAnswers_FailsWithinSeconds()
    {
        var daemon = new Daemon().Answer("GetSystemInfoAsync", _ => new TaskCompletionSource<SystemInfoResponse>().Task);
        var containers = new DockerContainers(daemon.Client, Options.Create(new SandboxOptions()), NullLogger<DockerContainers>.Instance);

        var run = containers.RunAsync("print(1)", [], TimeSpan.FromSeconds(5), CancellationToken.None);
        var first = await Task.WhenAny(run, Task.Delay(DockerContainers.AnswerWait + TimeSpan.FromSeconds(3)));

        Assert.Same(run, first);
        Assert.DoesNotContain("CreateContainerAsync", daemon.Calls);
    }
}
