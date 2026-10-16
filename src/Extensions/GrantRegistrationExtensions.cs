using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snail.MCP.Sandbox.Application;

namespace Snail.MCP.Sandbox.Extensions;

/// <summary>The grant of a session, checked where calls arrive.</summary>
public static class GrantRegistrationExtensions
{
    extension(IMcpServerBuilder builder)
    {
        /// <summary>Refuses a tool the session was not granted.</summary>
        /// <remarks>
        /// At the door rather than inside the tools, for the same reason as in the other servers of this
        /// fleet: a check that lives in the tools holds only until somebody adds one that forwards a call.
        /// This server forwards none today, and the check costs nothing while that stays true.
        /// </remarks>
        public IMcpServerBuilder RefuseWhatWasNotGranted()
        {
            builder.WithRequestFilters(filters => filters.AddCallToolFilter(Granted));

            return builder;
        }
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> Granted(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var grant = context.Server.Services?.GetService<SessionGrant>();

            if (context.Params?.Name is not { } name || grant is null || grant.Allows(name))
                return await next(context, cancellationToken);

            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = grant.Refusal(name) }]
            };
        };
}
