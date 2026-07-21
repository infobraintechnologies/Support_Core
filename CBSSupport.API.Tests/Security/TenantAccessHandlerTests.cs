using System.Security.Claims;
using CBSSupport.API.Security;
using Microsoft.AspNetCore.Authorization;

namespace CBSSupport.API.Tests.Security;

public sealed class TenantAccessHandlerTests
{
    [Fact]
    public async Task HandleAsync_AdminAccessingTenantResource_Succeeds()
    {
        var context = CreateContext(CreatePrincipal(Roles.Admin), new TenantResource(42));

        await new TenantAccessHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_ClientAccessingOwnTenant_Succeeds()
    {
        var context = CreateContext(
            CreatePrincipal(Roles.Client, new Claim(CustomClaimTypes.ClientId, "42")),
            new TenantResource(42));

        await new TenantAccessHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_ClientUsingLegacyClaimForOwnTenant_Succeeds()
    {
        var context = CreateContext(
            CreatePrincipal(Roles.Client, new Claim(CustomClaimTypes.LegacyClientId, "42")),
            new TenantResource(42));

        await new TenantAccessHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_ClientAccessingAnotherTenant_DoesNotSucceed()
    {
        var context = CreateContext(
            CreatePrincipal(Roles.Client, new Claim(CustomClaimTypes.ClientId, "7")),
            new TenantResource(42));

        await new TenantAccessHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public async Task HandleAsync_ClientWithInvalidTenantClaim_DoesNotSucceed(string? claimValue)
    {
        var claims = claimValue is null
            ? Array.Empty<Claim>()
            : [new Claim(CustomClaimTypes.ClientId, claimValue)];
        var context = CreateContext(CreatePrincipal(Roles.Client, claims), new TenantResource(42));

        await new TenantAccessHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_ClientWithConflictingTenantClaims_DoesNotSucceed()
    {
        var context = CreateContext(
            CreatePrincipal(
                Roles.Client,
                new Claim(CustomClaimTypes.ClientId, "42"),
                new Claim(CustomClaimTypes.LegacyClientId, "99")),
            new TenantResource(42));

        await new TenantAccessHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_IdentityWithoutSupportedRole_DoesNotSucceed()
    {
        var context = CreateContext(
            CreatePrincipal("User", new Claim(CustomClaimTypes.ClientId, "42")),
            new TenantResource(42));

        await new TenantAccessHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task HandleAsync_AdminAccessingInvalidTenantResource_DoesNotSucceed(long resourceClientId)
    {
        var context = CreateContext(CreatePrincipal(Roles.Admin), new TenantResource(resourceClientId));

        await new TenantAccessHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    private static AuthorizationHandlerContext CreateContext(
        ClaimsPrincipal principal,
        TenantResource resource)
    {
        return new AuthorizationHandlerContext(
            [TenantAccessRequirement.Instance],
            principal,
            resource);
    }

    private static ClaimsPrincipal CreatePrincipal(string role, params Claim[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Append(new Claim(ClaimTypes.Role, role)),
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}
