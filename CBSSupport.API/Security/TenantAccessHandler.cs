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

        if (context.User.IsInRole(Roles.Admin))
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
}
