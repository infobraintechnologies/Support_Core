using Microsoft.AspNetCore.SignalR;
using CBSSupport.Shared.Data;

namespace CBSSupport.API.Security;

public interface IActiveHubConnectionRegistry
{
    void Register(HubCallerContext context);

    void Remove(string connectionId);

    void AbortAccount(AccountReference account);

    IReadOnlyCollection<ActiveHubConnection> GetConnections();
}

public sealed record ActiveHubConnection(
    string ConnectionId,
    HubCallerContext Context);
