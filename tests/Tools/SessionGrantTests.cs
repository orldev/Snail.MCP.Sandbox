using Snail.MCP.Sandbox.Tools;
using System.Reflection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>
/// The platform checks a role's allow list on its side, but the server is the one place that cannot be gone around.
/// No tool here forwards calls, so there is no hole; the grant is kept all the same, because
/// a fleet where one server ignores the list is a fleet where the list means "usually".
/// </summary>
public sealed class SessionGrantTests(SandboxServer factory) : IClassFixture<SandboxServer>
{
    private static async Task<McpClient> OpenAsync(SandboxServer factory, string query)
    {
        var client = factory.CreateClient();

        return await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(client.BaseAddress!, $"/mcp{query}") }, client));
    }

    [Fact]
    public async Task A_tool_the_session_was_not_granted_is_refused()
    {
        await using var client = await OpenAsync(factory, "?tool=sandbox_diagnose");

        var refused = await client.CallToolAsync("sandbox_run_python", new Dictionary<string, object?> { ["code"] = "print(1)" });

        Assert.Equal(true, refused.IsError);
        Assert.Contains("was not granted",
            string.Join(string.Empty, refused.Content.OfType<TextContentBlock>().Select(block => block.Text)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A granted tool reaches itself. It may well answer with a refusal — in this fixture the node
    /// has no runsc, and sandbox_diagnose says so — but a refusal of the grant looks different, and that
    /// is what is checked here: not "it worked" but "it got through".
    /// </summary>
    [Fact]
    public async Task A_granted_tool_reaches_itself()
    {
        await using var client = await OpenAsync(factory, "?tool=sandbox_diagnose");

        Assert.DoesNotContain("was not granted", await DiagnoseAsync(client), StringComparison.Ordinal);
    }

    /// <summary>A session that named no tools uses all of them: an empty list always meant "everything".</summary>
    [Fact]
    public async Task A_session_that_names_no_tool_may_use_them_all()
    {
        await using var client = await OpenAsync(factory, string.Empty);

        Assert.DoesNotContain("was not granted", await DiagnoseAsync(client), StringComparison.Ordinal);
    }

    private static async Task<string> DiagnoseAsync(McpClient client)
    {
        var answer = await client.CallToolAsync("sandbox_diagnose", new Dictionary<string, object?>());

        return string.Join(string.Empty, answer.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }

    /// <summary>
    /// A tool's name is part of the contract with the outside. The prefix is worn by what is named after the server's subject;
    /// the fleet's mechanics (find_tool, run_tools, run_program) wear none, and here there are simply none of them.
    /// </summary>
    [Fact]
    public void Every_tool_is_named_after_what_this_server_is_for()
    {
        string[] machinery = ["find_tool", "run_program", "run_tools"];

        var names = typeof(RunTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .ToList();

        Assert.NotEmpty(names);
        Assert.All(names.Except(machinery), name => Assert.StartsWith("sandbox_", name, StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => machinery.Any(known => name.EndsWith("_" + known, StringComparison.Ordinal)));
    }
}
