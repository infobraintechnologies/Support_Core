using System.Globalization;
using System.Security.Claims;
using CBSSupport.Shared.Contracts;

namespace CBSSupport.API.Security;

public static class ClaimsPrincipalExtensions
{
    private const string LegacyUserIdClaimType = "UserId";

    public static bool TryGetClientId(this ClaimsPrincipal principal, out long clientId)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return TryGetConsistentPositiveInt64(
            principal,
            [CustomClaimTypes.ClientId, CustomClaimTypes.LegacyClientId],
            out clientId);
    }

    public static long GetRequiredClientId(this ClaimsPrincipal principal)
    {
        if (principal.TryGetClientId(out var clientId))
        {
            return clientId;
        }

        throw new UnauthorizedAccessException("The authenticated principal has no valid tenant claim.");
    }

    public static bool TryGetUserId(this ClaimsPrincipal principal, out long userId)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return TryGetConsistentPositiveInt64(
            principal,
            [ClaimTypes.NameIdentifier, JwtClaimTypes.Subject, LegacyUserIdClaimType],
            out userId);
    }

    public static long GetRequiredUserId(this ClaimsPrincipal principal)
    {
        if (principal.TryGetUserId(out var userId))
        {
            return userId;
        }

        throw new UnauthorizedAccessException("The authenticated principal has no valid user identifier claim.");
    }

    /// <summary>Builds the tenant-scoped conversation actor from trusted claims.</summary>
    public static ConversationActor GetConversationActor(this ClaimsPrincipal principal)
    {
        var userId = principal.GetRequiredUserId();
        var isAdmin = principal.IsInRole(Roles.Admin);
        return new ConversationActor(
            userId,
            isAdmin ? null : principal.GetRequiredClientId(),
            isAdmin,
            principal.FindFirstValue(ClaimTypes.Name)
                ?? principal.Identity?.Name
                ?? $"User {userId}");
    }

    private static bool TryGetConsistentPositiveInt64(
        ClaimsPrincipal principal,
        IReadOnlyCollection<string> claimTypes,
        out long value)
    {
        value = default;
        var found = false;

        foreach (var claimType in claimTypes)
        {
            foreach (var claim in principal.FindAll(claimType))
            {
                if (!long.TryParse(
                        claim.Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsedValue)
                    || parsedValue <= 0)
                {
                    value = default;
                    return false;
                }

                if (found && parsedValue != value)
                {
                    value = default;
                    return false;
                }

                value = parsedValue;
                found = true;
            }
        }

        return found;
    }
}
