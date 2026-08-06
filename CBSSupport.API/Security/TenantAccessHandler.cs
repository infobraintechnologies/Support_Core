using Microsoft.AspNetCore.Authorization;

namespace CBSSupport.API.Security;

public sealed class TenantAccessHandler
    : AuthorizationHandler<TenantAccessRequirement, TenantResource>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantAccessRequirement requirement,
        TenantResource resource)
    {
        if (resource.ClientId <= 0)
        {
            return Task.CompletedTask;
        }

        if (context.User.IsInRole(Roles.Admin)
            && HasAdministratorTenantAccess(context.User, resource.ClientId))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.IsInRole(Roles.Client)
            && context.User.TryGetClientId(out var clientId)
            && clientId == resource.ClientId)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool HasAdministratorTenantAccess(
        System.Security.Claims.ClaimsPrincipal principal,
        long clientId)
    {
        // Existing administrator accounts are global administrators until a
        // scoped tenant_access claim is issued. A scoped claim is an allow-list
        // and is checked on every request; the browser selection is never an
        // authority by itself.
        var claims = principal.FindAll(CustomClaimTypes.AdminTenantAccess).ToArray();
        if (claims.Length == 0)
        {
            return true;
        }

        return claims.Any(claim =>
            claim.Value == "*"
            || (long.TryParse(claim.Value, out var allowedClientId)
                && allowedClientId == clientId));
    }
}
