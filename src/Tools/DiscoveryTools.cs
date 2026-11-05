using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Snail.MCP.Sandbox.Tools;

/// <summary>Finding the way in: which tool does a thing, and what it takes to call it.</summary>
/// <remarks>
/// Named <c>find_tool</c> without this server's prefix, as in the other servers of this fleet: a model that
/// learned the question on one of them asks it the same way here. Reading the catalog changes nothing, and
/// the catalog is the server's own, so nothing outside the call is reached.
/// </remarks>
[McpServerToolType]
public sealed class DiscoveryTools(ToolIndex index)
{
    /// <summary>The tools that do what the task says, best first, or every tool when none matches.</summary>
    [McpServerTool(Name = "find_tool", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.FindTool)]
    public CallToolResult Find(
        [Description(ToolDescriptions.FindToolTask)] string task,
        [Description(ToolDescriptions.FindToolLimit)] int limit = 5) =>
        ToolResponse.Success(index.Answer(task, limit));
}
