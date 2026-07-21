using System.Collections.Concurrent;
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

    public IReadOnlyCollection<ActiveHubConnection> GetConnections() =>
        [.. _connections.Values];
}
