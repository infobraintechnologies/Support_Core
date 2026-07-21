using System.Security.Claims;
using CBSSupport.API.Security;

namespace CBSSupport.API.Tests.Security;

public sealed class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void TryGetClientId_CanonicalPositiveClaim_ReturnsClientId()
    {
        var principal = CreatePrincipal(new Claim(CustomClaimTypes.ClientId, "42"));

        var found = principal.TryGetClientId(out var clientId);

        Assert.True(found);
        Assert.Equal(42, clientId);
    }

    [Fact]
    public void TryGetClientId_LegacyPositiveClaim_ReturnsClientId()
    {
        var principal = CreatePrincipal(new Claim(CustomClaimTypes.LegacyClientId, "42"));

        var found = principal.TryGetClientId(out var clientId);

        Assert.True(found);
        Assert.Equal(42, clientId);
    }

    [Fact]
    public void TryGetClientId_CanonicalAndLegacyClaimsAgree_ReturnsClientId()
    {
        var principal = CreatePrincipal(
            new Claim(CustomClaimTypes.ClientId, "42"),
            new Claim(CustomClaimTypes.LegacyClientId, "42"));

        var found = principal.TryGetClientId(out var clientId);

        Assert.True(found);
        Assert.Equal(42, clientId);
    }

    [Fact]
    public void TryGetClientId_CanonicalAndLegacyClaimsConflict_ReturnsFalse()
    {
        var principal = CreatePrincipal(
            new Claim(CustomClaimTypes.ClientId, "42"),
            new Claim(CustomClaimTypes.LegacyClientId, "99"));

        var found = principal.TryGetClientId(out var clientId);

        Assert.False(found);
        Assert.Equal(0, clientId);
    }

    [Fact]
    public void TryGetClientId_DuplicateCanonicalClaimsConflict_ReturnsFalse()
    {
        var principal = CreatePrincipal(
            new Claim(CustomClaimTypes.ClientId, "42"),
            new Claim(CustomClaimTypes.ClientId, "99"));

        var found = principal.TryGetClientId(out var clientId);

        Assert.False(found);
        Assert.Equal(0, clientId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("9223372036854775808")]
    [InlineData("not-a-number")]
    public void TryGetClientId_InvalidClaim_ReturnsFalse(string claimValue)
    {
        var principal = CreatePrincipal(new Claim(CustomClaimTypes.ClientId, claimValue));

        var found = principal.TryGetClientId(out var clientId);

        Assert.False(found);
        Assert.Equal(0, clientId);
    }

    [Fact]
    public void TryGetClientId_MissingClaim_ReturnsFalse()
    {
        var principal = CreatePrincipal();

        var found = principal.TryGetClientId(out var clientId);

        Assert.False(found);
        Assert.Equal(0, clientId);
    }

    [Fact]
    public void GetRequiredClientId_InvalidClaim_ThrowsUnauthorizedAccessException()
    {
        var principal = CreatePrincipal(new Claim(CustomClaimTypes.ClientId, "0"));

        Assert.Throws<UnauthorizedAccessException>(() => principal.GetRequiredClientId());
    }

    [Fact]
    public void TryGetUserId_NameIdentifierClaim_ReturnsUserId()
    {
        var principal = CreatePrincipal(new Claim(ClaimTypes.NameIdentifier, "7"));

        var found = principal.TryGetUserId(out var userId);

        Assert.True(found);
        Assert.Equal(7, userId);
    }

    [Fact]
    public void TryGetUserId_SubjectClaim_ReturnsUserId()
    {
        var principal = CreatePrincipal(new Claim("sub", "7"));

        var found = principal.TryGetUserId(out var userId);

        Assert.True(found);
        Assert.Equal(7, userId);
    }

    [Fact]
    public void TryGetUserId_LegacyClaim_ReturnsUserId()
    {
        var principal = CreatePrincipal(new Claim("UserId", "7"));

        var found = principal.TryGetUserId(out var userId);

        Assert.True(found);
        Assert.Equal(7, userId);
    }

    [Fact]
    public void TryGetUserId_IdentifierClaimsConflict_ReturnsFalse()
    {
        var principal = CreatePrincipal(
            new Claim(ClaimTypes.NameIdentifier, "7"),
            new Claim("sub", "8"),
            new Claim("UserId", "7"));

        var found = principal.TryGetUserId(out var userId);

        Assert.False(found);
        Assert.Equal(0, userId);
    }

    [Fact]
    public void GetRequiredUserId_MissingClaim_ThrowsUnauthorizedAccessException()
    {
        var principal = CreatePrincipal();

        Assert.Throws<UnauthorizedAccessException>(() => principal.GetRequiredUserId());
    }

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}
