using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Tests.Adapters;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>The sweep at start against a daemon that is there and says nothing.</summary>
public sealed class LeftoverContainersChaosTests
{
    /// <summary>
    /// Breaks the start: every other call to the daemon is held to <see cref="DockerContainers.AnswerWait"/>, but the sweep
    /// waits as long as the client does — the longest test run plus its grace, thirty-one minutes — and the host's start waits
    /// with it. A daemon hung when the server starts holds the server unstarted for that long, where the remark promises
    /// «logged and left».
    /// </summary>
    [Fact]
    public async Task StartAsync_DaemonThatNeverAnswers_ReturnsWithinTheAnswerWait()
    {
        var never = new TaskCompletionSource<IList<ContainerListResponse>>();
        var daemon = new Daemon().Answer("ListContainersAsync", _ => never.Task);
        var sweep = new LeftoverContainers(daemon.Client, NullLogger<LeftoverContainers>.Instance);

        var started = sweep.StartAsync(CancellationToken.None);
        var first = await Task.WhenAny(started, Task.Delay(DockerContainers.AnswerWait + TimeSpan.FromSeconds(2)));

        Assert.Same(started, first);
    }
}
