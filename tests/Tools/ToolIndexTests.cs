using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Tools;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>
/// What a model asks when it does not know the name of the thing it wants. The index answers in this
/// server's own words, with the arguments each tool takes — and only with the tools the session may call,
/// because naming one the door will refuse is a wrong answer that costs a round trip to find out.
/// </summary>
public sealed class ToolIndexTests
{
    private readonly ToolIndex _index = IndexGranted();

    /// <summary>An index answering a session that named those tools; naming none means all of them.</summary>
    private static ToolIndex IndexGranted(params string[] granted)
    {
        var context = new DefaultHttpContext();

        if (granted.Length > 0)
            context.Request.QueryString = new QueryString("?" + string.Join('&', granted.Select(name => $"{SessionGrant.Parameter}={name}")));

        return new ToolIndex(new SessionGrant(new HttpContextAccessor { HttpContext = context }));
    }

    [Theory]
    [InlineData("draw a chart from this table", "sandbox_run_python")]
    [InlineData("run this python program", "sandbox_run_python")]
    [InlineData("reshape a csv into a dataframe", "sandbox_run_python")]
    [InlineData("is the node broken or is it my program", "sandbox_diagnose")]
    [InlineData("the docker daemon on the node", "sandbox_diagnose")]
    public void A_task_in_a_models_own_words_finds_the_tool_that_does_it(string task, string expected) =>
        Assert.Equal(expected, _index.Find(task, 3)[0].Name);

    /// <summary>A word in one form has to find the tool known by another: plot, plotting, plotted.</summary>
    [Fact]
    public void A_form_of_a_word_finds_the_tool_known_by_another_form() =>
        Assert.Equal("sandbox_run_python", _index.Find("plotting the results", 1)[0].Name);

    /// <summary>Three tools fit anywhere. A wrong answer costs more than the whole list does.</summary>
    [Fact]
    public void A_question_in_no_words_of_this_server_is_answered_with_every_tool()
    {
        var answer = _index.Answer("xyzzy plugh", 5);

        Assert.False(answer["isMatched"]!.GetValue<bool>());
        Assert.NotNull(answer["note"]);
        Assert.Equal(ToolIndex.All.Count, answer["tools"]!.AsArray().Count);
    }

    /// <summary>The arguments and which of them are required are what a tool list read in a hurry does not carry.</summary>
    [Fact]
    public void The_answer_carries_every_argument_the_tool_takes()
    {
        var found = _index.Answer("run this python program", 1)["tools"]![0]!;

        Assert.Equal("sandbox_run_python", found["name"]!.GetValue<string>());

        var parameters = found["parameters"]!.AsArray();
        var code = parameters.Single(parameter => parameter!["name"]!.GetValue<string>() == "code")!;
        var timeout = parameters.Single(parameter => parameter!["name"]!.GetValue<string>() == "timeoutSeconds")!;

        Assert.True(code["isRequired"]!.GetValue<bool>());
        Assert.Equal("string", code["type"]!.GetValue<string>());
        Assert.NotEmpty(code["description"]!.GetValue<string>());

        Assert.False(timeout["isRequired"]!.GetValue<bool>());
        Assert.Equal("integer", timeout["type"]!.GetValue<string>());
    }

    /// <summary>A session granted two tools is told about two, however well the third one answers.</summary>
    [Fact]
    public void A_tool_the_session_was_not_granted_is_not_offered_as_the_answer()
    {
        var index = IndexGranted("find_tool", "sandbox_diagnose");

        Assert.DoesNotContain("sandbox_run_python", Named(index.Answer("draw a chart from this table", 5)));

        var whole = index.Answer("xyzzy plugh", 5);

        Assert.False(whole["isMatched"]!.GetValue<bool>());
        Assert.Equal(["find_tool", "sandbox_diagnose"], [.. Named(whole).Order(StringComparer.Ordinal)]);
    }

    private static IReadOnlyList<string> Named(JsonObject answer) =>
        [.. answer["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>())];

    /// <summary>
    /// The words a tool is searched by are written beside the tools rather than in them, so nothing in
    /// the compiler notices when a tool is renamed or added and they are not. This does.
    /// </summary>
    [Fact]
    public void Every_tool_is_known_by_other_words_than_its_name_and_by_no_words_of_a_tool_that_is_gone() =>
        Assert.Equal(
            [.. ToolIndex.All.Select(entry => entry.Name).Order(StringComparer.Ordinal)],
            [.. ToolIndex.Meant().Order(StringComparer.Ordinal)]);

    [Fact]
    public void A_word_in_a_name_outweighs_the_same_word_in_a_description() =>
        Assert.True(
            Vocabulary.Score("python", "sandbox_run_python", "")
            > Vocabulary.Score("python", "sandbox_diagnose", "whether the node can run the python a model wrote"));

    /// <summary>The words every question is made of say nothing about which tool it wants.</summary>
    [Fact]
    public void The_common_words_of_a_question_score_nothing() =>
        Assert.Equal(0.0, Vocabulary.Score("what is it that I have in there", "sandbox_diagnose", ToolIndex.All[0].Description));

    /// <summary>A word the index knows a tool by is worth more than the same word met in someone else's description.</summary>
    [Fact]
    public void A_word_the_index_knows_a_tool_by_outweighs_a_description_that_merely_mentions_it() =>
        Assert.True(
            Vocabulary.Score("broken", "sandbox_diagnose", ToolIndex.Meant("sandbox_diagnose"), "")
            > Vocabulary.Score("broken", "sandbox_run_python", ToolIndex.Meant("sandbox_run_python"), "a program of your own that is broken fails here too"));
}
