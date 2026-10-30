using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Snail.MCP.Sandbox.Application;

namespace Snail.MCP.Sandbox.Tools;

/// <summary>Every tool of this server as an entry: what it is called, what it takes, and what each argument means.</summary>
/// <remarks>
/// Read from the tools themselves rather than written a second time beside them, because a catalog kept by
/// hand is a catalog that is wrong by the second release. Its purpose is not to hide the tools — two of them
/// fit in any context window — but to answer a question asked in words the tool list is not written in, and
/// to answer it in one call rather than by trying a tool and reading the refusal.
/// <para>
/// A session that was granted only some tools is answered with only those: telling a model to call a tool the
/// door will refuse is a wrong answer that costs a round trip to find out.
/// </para>
/// </remarks>
public sealed class ToolIndex(SessionGrant grant)
{
    /// <summary>Every tool of this server: its own first, then the ones the whole fleet shares.</summary>
    public static readonly IReadOnlyList<ToolEntry> All = Read();

    /// <summary>The classes that declare the tools, named here: a native build keeps the methods of a class it is told of, and reads no assembly for them.</summary>
    /// <remarks>A guard test holds this list to every class carrying <see cref="McpServerToolTypeAttribute"/>, so a class added beside them cannot be left out.</remarks>
    internal static readonly IReadOnlyList<Type> Declaring = [typeof(RunTools), typeof(DiscoveryTools)];

    /// <summary>The prefix this server's own tools carry; the fleet's shared tools carry the same name everywhere.</summary>
    private const string Prefix = "sandbox_";

    /// <summary>
    /// The words a caller reaches for that the tool is not named with and its description need not carry.
    /// They live here rather than in the descriptions because the descriptions are read by every session
    /// whether or not anyone searches, and these are read only when someone does.
    /// </summary>
    private static readonly Dictionary<string, string> Meanings = new(StringComparer.Ordinal)
    {
        ["sandbox_run_python"] =
            "execute script compute calculate analyse chart plot graph figure table report convert parse "
            + "pandas numpy matplotlib duckdb openpyxl excel csv json dataframe statistics fit model",
        ["sandbox_run_tests"] =
            "test tests unit build compile verify green red failing passing ci restore pytest xunit nunit mstest jest "
            + "npm dotnet solution csproj project suite regression",
        ["sandbox_diagnose"] =
            "health ready broken failing available runtime docker daemon image node check why",
        ["find_tool"] = "search discover name parameters arguments how"
    };

    /// <summary>The tools this session may call, in the order of the catalog.</summary>
    public IReadOnlyList<ToolEntry> Visible => [.. All.Where(entry => grant.Allows(entry.Name))];

    /// <summary>The tools that answer a question, best first; every one of them when none does.</summary>
    public IReadOnlyList<ToolEntry> Find(string? task, int limit)
    {
        var visible = Visible;
        var scored = visible.Select(entry => (Entry: entry, Score: Vocabulary.Score(task ?? string.Empty, entry.Name, Meant(entry.Name), entry.Description)));
        var answering = scored.Where(candidate => candidate.Score > 0);
        var ranked = answering
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Entry.Name, StringComparer.Ordinal);

        return [.. ranked.Take(Math.Clamp(limit, 1, Math.Max(visible.Count, 1))).Select(candidate => candidate.Entry)];
    }

    /// <summary>The answer <c>find_tool</c> gives: what matched, or the whole catalog when nothing did.</summary>
    public JsonObject Answer(string? task, int limit)
    {
        var found = Find(task, limit);

        return new JsonObject
        {
            ["task"] = task,
            ["isMatched"] = found.Count > 0,
            ["note"] = found.Count > 0 ? null : "nothing in this server's words matches; these are all of its tools",
            ["tools"] = new JsonArray([.. (found.Count > 0 ? found : Visible).Select(Describe)])
        };
    }

    /// <summary>The other words this tool is asked for by, or none when the index knows no others.</summary>
    internal static string? Meant(string name) => Meanings.GetValueOrDefault(name);

    /// <summary>The tools the index knows other words for; a guard test keeps these the tools that exist.</summary>
    internal static IEnumerable<string> Meant() => Meanings.Keys;

    private static JsonNode Describe(ToolEntry entry) => new JsonObject
    {
        ["name"] = entry.Name,
        ["description"] = entry.Description,
        ["parameters"] = new JsonArray([.. entry.Parameters.Select(parameter => (JsonNode)new JsonObject
        {
            ["name"] = parameter.Name,
            ["type"] = parameter.Type,
            ["isRequired"] = parameter.IsRequired,
            ["description"] = parameter.Description
        })])
    };

    /// <summary>The tools of the declaring classes, this server's own first and each group by name.</summary>
    private static IReadOnlyList<ToolEntry> Read()
    {
        IEnumerable<MethodInfo> methods = [.. Methods<RunTools>(), .. Methods<DiscoveryTools>()];
        var tools = methods.Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
        var entries = tools.Select(Entry);

        return [.. entries.OrderBy(Position).ThenBy(entry => entry.Name, StringComparer.Ordinal)];
    }

    /// <summary>The public methods of a class named by type, which is what keeps them in a native build.</summary>
    private static MethodInfo[] Methods<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>() =>
        typeof(T).GetMethods(BindingFlags.Public | BindingFlags.Instance);

    /// <summary>What this server does comes before how to ask it what it does.</summary>
    private static int Position(ToolEntry entry) => entry.Name.StartsWith(Prefix, StringComparison.Ordinal) ? 0 : 1;

    private static ToolEntry Entry(MethodInfo tool) => new(
        tool.GetCustomAttribute<McpServerToolAttribute>()!.Name!,
        tool.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
        [.. tool.GetParameters().Where(IsFillable).Select(Argument)]);

    /// <summary>The parameters the model fills; the cancellation token is the SDK's to fill.</summary>
    private static bool IsFillable(ParameterInfo parameter) => parameter.ParameterType != typeof(CancellationToken);

    private static ToolArgument Argument(ParameterInfo parameter) => new(
        parameter.Name!,
        Shape(parameter.ParameterType),
        !parameter.HasDefaultValue,
        parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty);

    /// <summary>The JSON shape of a parameter, as a caller filling it has to write it.</summary>
    /// <remarks>An optional number arrives as <c>int?</c>, and what the caller writes is a number either way.</remarks>
    private static string Shape(Type type)
    {
        var given = Nullable.GetUnderlyingType(type) ?? type;

        return given switch
        {
            _ when given == typeof(string) => "string",
            _ when given == typeof(bool) => "boolean",
            _ when given == typeof(int) => "integer",
            _ when given == typeof(string[]) => "string[]",
            _ => "object"
        };
    }
}

/// <summary>One tool as the index knows it.</summary>
public sealed record ToolEntry(string Name, string Description, IReadOnlyList<ToolArgument> Parameters);

/// <summary>One argument of a tool, with the words the model reads before filling it.</summary>
public sealed record ToolArgument(string Name, string Type, bool IsRequired, string Description);
