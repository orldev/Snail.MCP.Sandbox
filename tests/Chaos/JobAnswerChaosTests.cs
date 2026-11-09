using ModelContextProtocol.Protocol;
using Snail.MCP.Sandbox.Tools;
using Snail.Sdk.Mcp.Hosting.Jobs;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>What the model is told when a job ended badly.</summary>
public sealed class JobAnswerChaosTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Breaks the attribution: a job that failed on a download — the object store refused the connection, the link timed out —
    /// ends Failed with that exception's message, and the tool blames a shutdown and tells the model to start again,
    /// which will fail the same way. The failure's own words have to reach the model, and the hint has to fit them.
    /// </summary>
    [Fact]
    public void Run_JobFailedOnADownloadError_DoesNotBlameAShutdown()
    {
        var failed = new JobReport("job://sandbox/python/abc", JobState.Failed, Now, Now, null, "Connection refused (objects.example:9000)", null);

        var result = ToolResponse.Run(failed);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;

        Assert.True(result.IsError);
        Assert.Contains("Connection refused", text, StringComparison.Ordinal);
        Assert.DoesNotContain("shutting down", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Breaks the work itself: a run whose work throws — the daemon gone mid-run — ends its job as the server's own failure,
    /// of the type <c>internal</c>, never <c>failed</c> as a refusal of the program would read, and the hint says the sandbox failed.
    /// </summary>
    [Fact]
    public async Task Run_WorkThrows_IsTheSandboxsOwnFailure()
    {
        var report = await new Jobs("sandbox").Start("python", null, (_, _) => Task.FromException<Answer<RunSummary>>(new InvalidOperationException("the daemon went away")), ToolResponse.RunResult).Completion;

        var result = ToolResponse.Run(report);

        Assert.True(result.IsError);
        Assert.Equal("internal", result.Type());
        Assert.Equal(Messages.SandboxFailed, result.Hint());
    }
}
