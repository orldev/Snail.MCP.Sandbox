using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;

namespace Snail.MCP.Sandbox.Tools;

/// <summary>Running code: one program, its files, its time — and whether the node can run anything at all.</summary>
/// <remarks>
/// Both are declared read-only: whatever the program does happens inside a container that is gone when the call
/// returns, and the diagnosis only asks the daemon, so nothing outside the call changes.
/// </remarks>
[McpServerToolType]
public sealed class RunTools(Runs runs, IContainers containers)
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
}
