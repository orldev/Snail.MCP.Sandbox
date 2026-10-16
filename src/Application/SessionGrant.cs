
namespace Snail.MCP.Sandbox.Application;

/// <summary>Which tools this session may use, read from the address it opened.</summary>
/// <remarks>
/// The platform gives a role a list of tools, and the server is the only place that list cannot be
/// talked around. The other servers of this fleet learned that the hard way — a role granted one tool
/// that forwards calls reached every tool they had. This one forwards nothing, so there is no hole to
/// close here; the grant is honoured anyway, because a fleet where one server ignores the list is a
/// fleet where the list means «usually».
/// <para>
/// Read straight from the request rather than through a port of its own: this server has one shape and
/// only one — it answers over HTTP and never as a child process — so an abstraction over two shapes
/// would be ceremony with a single implementation. A session that names no tool may use them all.
/// </para>
/// <para>
/// The grant is read from the request a call arrives on, not from the one that opened the session: a client sends
/// every call to the address it was registered with, so the two are the same, and it is the client — the platform —
/// that holds the address, never the model.
/// </para>
/// </remarks>
public sealed class SessionGrant(IHttpContextAccessor requests)
{
    /// <summary>What the address calls a tool it allows; repeat it for each one.</summary>
    public const string Parameter = "tool";

    /// <summary>The tools this session named, empty when it named none and may use everything.</summary>
    public IReadOnlyList<string> All =>
        requests.HttpContext is { } request
            ?
            [
                .. request.Request.Query[Parameter]
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            ]
            : [];

    /// <summary>Whether this session may call that tool.</summary>
    public bool Allows(string name)
    {
        var granted = All;

        return granted.Count == 0 || granted.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The refusal, in the words a caller needs to stop asking.</summary>
    public string Refusal(string name) =>
        $"'{name}' was not granted to this session, which may use {string.Join(", ", All)}";
}
