using CBSSupport.API.Security;
using Microsoft.AspNetCore.SignalR;

namespace CBSSupport.API.Realtime;

public sealed class NamespacedUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        var principal = connection.User;
        if (!principal.TryGetUserId(out var userId)) return null;

        var isAdmin = principal.IsInRole(Roles.Admin);
        var isClient = principal.IsInRole(Roles.Client);
        if (isAdmin == isClient) return null;

        if (isAdmin) return RealtimeUserIds.Admin(userId);

        return principal.TryGetClientId(out var clientId)
            ? RealtimeUserIds.Client(clientId, userId)
            : null;
    }
}
