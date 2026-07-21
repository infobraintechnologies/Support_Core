using Microsoft.AspNetCore.SignalR;

namespace CBSSupport.API.Security;

public interface IActiveHubConnectionRegistry
{
    void Register(HubCallerContext context);

    void Remove(string connectionId);

    IReadOnlyCollection<ActiveHubConnection> GetConnections();
}

public sealed record ActiveHubConnection(
    string ConnectionId,
    HubCallerContext Context);
