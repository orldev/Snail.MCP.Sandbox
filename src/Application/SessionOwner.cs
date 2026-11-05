namespace Snail.MCP.Sandbox.Application;

/// <summary>Whose a job is: the MCP session that started it, read from the request a call arrives on.</summary>
/// <remarks>
/// One server serves every task of the platform, and the SDK keeps the last twenty ended jobs of each owner: with no
/// owner, every task's job counted as one owner's, and twenty tasks ending their runs after mine forgot my result
/// before I read it (chaos test, 30.10.2026). The session is the one identity this server has of a caller — it holds
/// no task — and it is the session that started a run that awaits it, so a job belongs to its session and another
/// session asking for its URI is told there is none. Read from the request like <see cref="SessionGrant"/>, and for
/// the same reason: the server has one shape, HTTP, and a port over it would be ceremony.
/// </remarks>
public sealed class SessionOwner(IHttpContextAccessor requests)
{
    /// <summary>The header Streamable HTTP carries the session in; the SDK's own name for it is not public.</summary>
    public const string Header = "Mcp-Session-Id";

    /// <summary>The session of the request being served, or null outside one.</summary>
    public string? Current =>
        requests.HttpContext?.Request.Headers[Header] is { Count: > 0 } session ? session.ToString() : null;
}
