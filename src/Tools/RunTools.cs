using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;

namespace Snail.MCP.Sandbox.Tools;

/// <summary>Running code: one program, its files, its time — and whether the node can run anything at all.</summary>
/// <remarks>
/// All are declared read-only: whatever the program or the tests do happens inside a container that is gone when the
/// call returns, and the diagnosis only asks the daemon, so nothing outside the call changes. Only the test run is open
/// to the world: its restore stage fetches packages when the deployment gives it a network to fetch them over.
/// </remarks>
[McpServerToolType]
public sealed class RunTools(Runs runs, TestRuns tests, IContainers containers)
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
        CancellationToken cancellationToken = default) =>
        ToolResponse.From(await runs.RunAsync(code, files, timeoutSeconds, cancellationToken));

    [McpServerTool(Name = "sandbox_run_tests", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description(ToolDescriptions.RunTests)]
    public async Task<CallToolResult> RunTestsAsync(
        [Description(ToolDescriptions.RunTestsProject)] string project,
        [Description(ToolDescriptions.RunTestsToolchain)] string? toolchain = null,
        [Description(ToolDescriptions.RunTestsTimeout)] int? timeoutSeconds = null,
        CancellationToken cancellationToken = default) =>
        ToolResponse.From(await tests.RunAsync(project, toolchain, timeoutSeconds, cancellationToken));
}
