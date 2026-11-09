using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.Sdk.Mcp.Hosting;
using Snail.Sdk.Mcp.Hosting.Jobs;
using Snail.Sdk.Mcp.Hosting.Sessions;
using Snail.Sdk.Mcp.Http;

namespace Snail.MCP.Sandbox.Tools;

/// <summary>Running code: one program, its files, its time — and whether the node can run anything at all.</summary>
/// <remarks>
/// <para>
/// All are declared read-only: whatever the program or the tests do happens inside a container that is gone when the
/// call returns, and the diagnosis only asks the daemon, so nothing outside the call changes. Only the test run is open
/// to the world: its restore stage fetches packages when the deployment gives it a network to fetch them over.
/// </para>
/// <para>
/// A run and a test run are jobs: the work outlasts a call — a build and its tests take minutes — so the tool starts
/// it and answers within the wait the client allows, with the answer when the work ended in time and with the job's
/// resource when it did not. The run goes on when the client that started it is gone, and ends with the server. The
/// job belongs to the session that started it (<c>JobOwners.Session</c>): that is who awaits it, and who the kept
/// results are counted for. A run that could not happen ends its job failed with the refusal's error, hint and type.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class RunTools(Runs runs, TestRuns tests, IContainers containers, Jobs jobs, IServiceProvider services)
{
    /// <summary>Whether the node can run code at all, without running any.</summary>
    [McpServerTool(Name = "sandbox_diagnose", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Diagnose)]
    public async Task<CallToolResult> DiagnoseAsync(CancellationToken cancellationToken = default) =>
        ToolResponse.From(await containers.DiagnoseAsync(cancellationToken));

    /// <summary>One Python program run in a container of its own, answered within the wait or as the job to read.</summary>
    [McpServerTool(Name = "sandbox_run_python", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(ToolDescriptions.RunPython)]
    public async Task<CallToolResult> RunPythonAsync(
        [Description(ToolDescriptions.RunPythonCode)] string code,
        [Description(ToolDescriptions.RunPythonFiles)] string[]? files = null,
        [Description(ToolDescriptions.RunPythonTimeout)] int? timeoutSeconds = null,
        [Description(ToolDescriptions.Wait)] int wait = 60,
        CancellationToken cancellationToken = default)
    {
        if (Waiting(wait) is { } refused)
            return refused;

        var answered = new TaskCompletionSource<Answer<RunOutcome>>();
        var job = jobs.Start(
            "python",
            JobOwners.Session(services),
            async (_, token) => Handed(answered, await runs.RunAsync(code, files, timeoutSeconds, token)).Map(outcome => AnswerFields.Of(outcome, withContent: true)),
            ToolResponse.RunResult);

        var report = await job.ForAsync(TimeSpan.FromSeconds(wait), cancellationToken);

        return ToolResponse.Run(report, await HandedAsync(answered));
    }

    /// <summary>One project restored, built and tested in a container of its own, answered within the wait or as the job to read.</summary>
    [McpServerTool(Name = "sandbox_run_tests", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description(ToolDescriptions.RunTests)]
    public async Task<CallToolResult> RunTestsAsync(
        [Description(ToolDescriptions.RunTestsProject)] string project,
        [Description(ToolDescriptions.RunTestsToolchain)] string? toolchain = null,
        [Description(ToolDescriptions.RunTestsTimeout)] int? timeoutSeconds = null,
        [Description(ToolDescriptions.Wait)] int wait = 60,
        CancellationToken cancellationToken = default)
    {
        if (Waiting(wait) is { } refused)
            return refused;

        var job = jobs.Start(
            "tests",
            JobOwners.Session(services),
            async (_, token) => (await tests.RunAsync(project, toolchain, timeoutSeconds, token)).Map(AnswerFields.Of),
            ToolResponse.TestResult);

        var report = await job.ForAsync(TimeSpan.FromSeconds(wait), cancellationToken);

        return ToolResponse.Tests(report);
    }

    /// <summary>The refusal of a wait outside what a call may ask for, or null when the wait is one.</summary>
    /// <remarks>The description promises a range, and it is held here, as timeoutSeconds is: the jobs would wait out a longer one, but a wait past the longest run and its grace is a mistake of the call.</remarks>
    private static CallToolResult? Waiting(int wait) =>
        wait is >= 0 and <= ToolDescriptions.LongestWait
            ? null
            : ToolAnswer.Failed($"wait was {wait}; a call may wait from 0 to {ToolDescriptions.LongestWait} seconds.", Messages.WaitOutsideTheLimit, ToolFailures.Arguments);

    /// <summary>The answer handed to the call that is waiting on it, before it becomes the job's result.</summary>
    /// <remarks>The call tells a run from its files' own bytes; the result, base64, is for a client that reads the job later.</remarks>
    private static T Handed<T>(TaskCompletionSource<T> answered, T answer)
    {
        answered.TrySetResult(answer);

        return answer;
    }

    /// <summary>The answer the work handed over, or null when it has not ended yet; never a wait.</summary>
    private static async Task<T?> HandedAsync<T>(TaskCompletionSource<T> answered) where T : class =>
        answered.Task.IsCompletedSuccessfully ? await answered.Task : null;
}
