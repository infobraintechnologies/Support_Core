using System.Security.Claims;
using CBSSupport.Shared.Data;

namespace CBSSupport.API.Security;

public sealed class AccountPrincipalValidator(
    IUserRepository userRepository,
    IAccountSecurityStampService securityStamps) : IAccountPrincipalValidator
{
    public async Task<bool> ValidateAsync(
        ClaimsPrincipal? principal,
        CancellationToken cancellationToken = default)
    {
        if (principal is null
            || !principal.TryGetUserId(out var userId)
            || !TryGetSingleRole(principal, out var role)
            || !TryGetSecurityStamp(principal, out var securityStamp))
        {
            return false;
        }

        return role switch
        {
            Roles.Admin when !principal.TryGetClientId(out _) =>
                await ValidateAdministratorAsync(userId, securityStamp, cancellationToken),
            Roles.Client when principal.TryGetClientId(out var clientId) =>
                await ValidateClientAsync(userId, clientId, securityStamp, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> ValidateAdministratorAsync(
        long userId,
        string securityStamp,
        CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        return user is { Status: true, DeactiveDate: null }
            && securityStamps.Matches(securityStamp, user.SecurityStamp);
    }

    private async Task<bool> ValidateClientAsync(
        long userId,
        long clientId,
        string securityStamp,
        CancellationToken cancellationToken)
    {
        var user = await userRepository.GetClientUserByIdAsync(clientId, userId, cancellationToken);
        return user is { Status: true, DeactiveDate: null }
            && securityStamps.Matches(securityStamp, user.SecurityStamp);
    }

    private static bool TryGetSingleRole(ClaimsPrincipal principal, out string role)
    {
        var roles = principal.Claims
            .Where(claim => claim.Type is ClaimTypes.Role or JwtClaimTypes.Role)
            .Select(claim => claim.Value)
            .ToArray();
        role = roles.Length == 1 ? roles[0] : string.Empty;
        return role is Roles.Admin or Roles.Client;
    }

    private static bool TryGetSecurityStamp(ClaimsPrincipal principal, out string securityStamp)
    {
        var stamps = principal.FindAll(CustomClaimTypes.SecurityStamp)
            .Select(claim => claim.Value)
            .ToArray();
        securityStamp = stamps.Length == 1 ? stamps[0] : string.Empty;
        return !string.IsNullOrWhiteSpace(securityStamp);
    }
}
