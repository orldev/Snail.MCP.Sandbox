using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.Sdk.Mcp.Hosting.Jobs;

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
/// resource when it did not. The run goes on when the client that started it is gone, and ends with the server.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class RunTools(Runs runs, TestRuns tests, IContainers containers, Jobs jobs)
{
    [McpServerTool(Name = "sandbox_diagnose", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Diagnose)]
    public async Task<CallToolResult> DiagnoseAsync(CancellationToken cancellationToken = default) =>
        ToolResponse.From(await containers.DiagnoseAsync(cancellationToken));

    [McpServerTool(Name = "sandbox_run_python", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(ToolDescriptions.RunPython)]
    public async Task<CallToolResult> RunPythonAsync(
        [Description(ToolDescriptions.RunPythonCode)] string code,
        [Description(ToolDescriptions.RunPythonFiles)] string[]? files = null,
        [Description(ToolDescriptions.RunPythonTimeout)] int? timeoutSeconds = null,
        [Description(ToolDescriptions.Wait)] int wait = 60,
        CancellationToken cancellationToken = default)
    {
        var job = jobs.Start("python", async (_, token) => ToolResponse.Answered(await runs.RunAsync(code, files, timeoutSeconds, token)), ToolResponse.RunResult);

        return ToolResponse.Run(await job.ForAsync(TimeSpan.FromSeconds(wait), cancellationToken));
    }

    [McpServerTool(Name = "sandbox_run_tests", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description(ToolDescriptions.RunTests)]
    public async Task<CallToolResult> RunTestsAsync(
        [Description(ToolDescriptions.RunTestsProject)] string project,
        [Description(ToolDescriptions.RunTestsToolchain)] string? toolchain = null,
        [Description(ToolDescriptions.RunTestsTimeout)] int? timeoutSeconds = null,
        [Description(ToolDescriptions.Wait)] int wait = 60,
        CancellationToken cancellationToken = default)
    {
        var job = jobs.Start("tests", async (_, token) => ToolResponse.Answered(await tests.RunAsync(project, toolchain, timeoutSeconds, token)), ToolResponse.TestResult);

        return ToolResponse.Tests(await job.ForAsync(TimeSpan.FromSeconds(wait), cancellationToken));
    }
}
