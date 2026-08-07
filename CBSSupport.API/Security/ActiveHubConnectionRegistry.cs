using System.Collections.Concurrent;
using CBSSupport.Shared.Data;
using Microsoft.AspNetCore.SignalR;

namespace CBSSupport.API.Security;

public sealed class ActiveHubConnectionRegistry : IActiveHubConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ActiveHubConnection> _connections = [];

    public void Register(HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _connections[context.ConnectionId] = new ActiveHubConnection(
            context.ConnectionId,
            context);
    }

    public void Remove(string connectionId) =>
        _connections.TryRemove(connectionId, out _);

    public void AbortAccount(AccountReference account)
    {
        foreach (var connection in _connections.Values)
        {
            if (!MatchesAccount(connection.Context.User, account))
            {
                continue;
            }

            connection.Context.Abort();
            _connections.TryRemove(connection.ConnectionId, out _);
        }
    }

    public IReadOnlyCollection<ActiveHubConnection> GetConnections() =>
        [.. _connections.Values];

    private static bool MatchesAccount(
        System.Security.Claims.ClaimsPrincipal? principal,
        AccountReference account)
    {
        if (principal is null
            || !principal.TryGetUserId(out var userId)
            || userId != account.UserId)
        {
            return false;
        }

        return account.Kind switch
        {
            AccountKind.Administrator =>
                principal.IsInRole(Roles.Admin) && !principal.IsInRole(Roles.Client),
            AccountKind.Client =>
                principal.IsInRole(Roles.Client) && !principal.IsInRole(Roles.Admin),
            _ => false
        };
    }
}
