using Microsoft.AspNetCore.Authorization;

namespace CBSSupport.API.Security;

public sealed class TenantAccessRequirement : IAuthorizationRequirement
{
    public static TenantAccessRequirement Instance { get; } = new();

    private TenantAccessRequirement()
    {
    }
}
