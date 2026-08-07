using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using CBSSupport.Shared.Data;

namespace CBSSupport.API.Security;

public static class SecurityAuditContext
{
    public static SecurityAuditEvent ForHttpRequest(
        HttpContext context,
        string action,
        string outcome,
        long? tenantId = null,
        string? targetKind = null,
        string? targetId = null,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        var actor = FromPrincipal(context.User, tenantId);
        return new SecurityAuditEvent(
            actor.TenantId,
            actor.ActorKind,
            actor.ActorUserId,
            targetKind,
            targetId,
            action,
            outcome,
            DateTimeOffset.UtcNow,
            Activity.Current?.Id ?? context.TraceIdentifier,
            MaskIp(context.Connection.RemoteIpAddress),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["transport"] = "http",
                ["method"] = context.Request.Method,
                ["endpoint"] = context.GetEndpoint()?.DisplayName
            },
            details);
    }

    public static SecurityAuditActor FromPrincipal(
        ClaimsPrincipal? principal,
        long? tenantId = null)
    {
        if (principal?.TryGetUserId(out var userId) != true)
        {
            return new(SecurityAuditActorKinds.Anonymous, null, null);
        }

        if (principal.IsInRole(Roles.Admin))
        {
            return new(SecurityAuditActorKinds.Admin, userId, tenantId);
        }

        return principal.IsInRole(Roles.Client)
            && principal.TryGetClientId(out var clientId)
            ? new(SecurityAuditActorKinds.Client, userId, clientId)
            : new(SecurityAuditActorKinds.Anonymous, null, null);
    }

    public static string? MaskIp(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var bytes = normalized.GetAddressBytes();
        var prefixLength = normalized.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 24 : 64;
        var byteCount = prefixLength / 8;
        for (var index = byteCount; index < bytes.Length; index++)
        {
            bytes[index] = 0;
        }

        return $"{new IPAddress(bytes)}/{prefixLength}";
    }
}

public sealed record SecurityAuditActor(
    string ActorKind,
    long? ActorUserId,
    long? TenantId);
