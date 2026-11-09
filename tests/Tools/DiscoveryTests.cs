using System.Text.Json.Nodes;
using Snail.MCP.Sandbox.Extensions;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>
/// What a model asks when it does not know the name of the thing it wants. <c>find_tool</c> is Snail.Sdk.Mcp's; what is
/// this server's are the words its tools are known by, and these tests hold that a task in a model's own words still finds
/// the tool that does it — and only among the tools the session may call.
/// </summary>
public sealed class DiscoveryTests(SandboxServer factory) : IClassFixture<SandboxServer>
{
    [Theory]
    [InlineData("draw a chart from this table", "sandbox_run_python")]
    [InlineData("run this python program", "sandbox_run_python")]
    [InlineData("reshape a csv into a dataframe", "sandbox_run_python")]
    [InlineData("plotting the results", "sandbox_run_python")]
    [InlineData("is the node broken or is it my program", "sandbox_diagnose")]
    [InlineData("the docker daemon on the node", "sandbox_diagnose")]
    [InlineData("build the solution and run its xunit suite", "sandbox_run_tests")]
    public async Task FindTool_ATaskInAModelsOwnWords_FindsTheToolThatDoesIt(string task, string expected)
    {
        var answer = await FindAsync(string.Empty, task);

        Assert.True(answer["isMatched"]!.GetValue<bool>());
        Assert.Equal(expected, Named(answer)[0]);
    }

    /// <summary>Four tools fit anywhere. A wrong answer costs more than the whole list does.</summary>
    [Fact]
    public async Task FindTool_AQuestionInNoWordsOfThisServer_IsAnsweredWithEveryTool()
    {
        var answer = await FindAsync(string.Empty, "xyzzy plugh");

        Assert.False(answer["isMatched"]!.GetValue<bool>());
        Assert.NotNull(answer["note"]);
        Assert.Equal(["find_tool", "sandbox_diagnose", "sandbox_run_python", "sandbox_run_tests"], Named(answer).Order(StringComparer.Ordinal));
    }

    /// <summary>The arguments and which of them are required are what a tool list read in a hurry does not carry.</summary>
    [Fact]
    public async Task FindTool_Answer_CarriesEveryArgumentTheToolTakes()
    {
        var found = (await FindAsync(string.Empty, "run this python program"))["tools"]![0]!;
        var parameters = found["parameters"]!.AsArray();
        var code = parameters.Single(parameter => parameter!["name"]!.GetValue<string>() == "code")!;
        var timeout = parameters.Single(parameter => parameter!["name"]!.GetValue<string>() == "timeoutSeconds")!;

        Assert.Equal("sandbox_run_python", found["name"]!.GetValue<string>());
        Assert.True(code["isRequired"]!.GetValue<bool>());
        Assert.Equal("string", code["type"]!.GetValue<string>());
        Assert.NotEmpty(code["description"]!.GetValue<string>());
        Assert.False(timeout["isRequired"]!.GetValue<bool>());
        Assert.Equal("integer", timeout["type"]!.GetValue<string>());
    }

    /// <summary>A session granted two tools is told about two, however well the third one answers.</summary>
    [Fact]
    public async Task FindTool_ASessionGrantedSomeTools_IsNotOfferedTheOthers()
    {
        const string granted = "?tool=find_tool&tool=sandbox_diagnose";

        var chart = await FindAsync(granted, "draw a chart from this table");
        var whole = await FindAsync(granted, "xyzzy plugh");

        Assert.DoesNotContain("sandbox_run_python", Named(chart));
        Assert.False(whole["isMatched"]!.GetValue<bool>());
        Assert.Equal(["find_tool", "sandbox_diagnose"], Named(whole).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The words a tool is searched by are written beside the tools rather than in them, so nothing in the compiler
    /// notices when a tool is renamed or added and they are not. This does.
    /// </summary>
    [Fact]
    public async Task Meanings_EveryToolListed_IsKnownByOtherWordsAndNoToolThatIsGone()
    {
        using var http = factory.CreateClient();
        await using var session = await HttpContractTests.SessionAsync(http);

        var listed = await session.ListToolsAsync();

        Assert.Equal(
            listed.Select(tool => tool.Name).Order(StringComparer.Ordinal),
            SandboxServerExtensions.Meanings.Keys.Order(StringComparer.Ordinal));
    }

    private async Task<JsonNode> FindAsync(string query, string task)
    {
        using var http = factory.CreateClient();
        await using var session = await HttpContractTests.SessionAsync(http, query);

        var answer = await session.CallToolAsync("find_tool", new Dictionary<string, object?> { ["task"] = task, ["limit"] = 3 });

        Assert.True(answer.IsOk(), answer.Text());

        return answer.Data()!;
    }

    private static IReadOnlyList<string> Named(JsonNode answer) =>
        [.. answer["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>())];
}
