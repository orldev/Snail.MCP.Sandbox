using Snail.MCP.Sandbox.Tools;
using System.Reflection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>
/// Белый список роли платформа проверяет у себя, но сервер — единственное место, которое нельзя обойти.
/// Здесь пересылающих инструментов нет, поэтому дыры и нет; выдача соблюдается всё равно, потому что
/// флот, где один сервер список игнорирует, — это флот, где список значит «обычно».
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
    /// Выданный инструмент доходит до самого себя. Ответить он может и отказом — в этой фикстуре узел
    /// без runsc, и sandbox_diagnose честно про это говорит, — но отказ выдачи выглядит иначе, и именно
    /// это здесь и проверяется: не «получилось», а «дошло».
    /// </summary>
    [Fact]
    public async Task A_granted_tool_reaches_itself()
    {
        await using var client = await OpenAsync(factory, "?tool=sandbox_diagnose");

        Assert.DoesNotContain("was not granted", await DiagnoseAsync(client), StringComparison.Ordinal);
    }

    /// <summary>Сессия, не назвавшая инструментов, пользуется всеми: пустой список и значил «всё».</summary>
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
    /// Имя инструмента — часть договора наружу. Префикс носит тот, кто назван предметом сервера; механика
    /// флота (find_tool, run_tools, run_program) префикса не носит, и здесь её просто нет.
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
