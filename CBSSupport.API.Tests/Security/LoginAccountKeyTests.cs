using CBSSupport.API.Security;
using System.Net;

namespace CBSSupport.API.Tests.Security;

public sealed class LoginAccountKeyTests
{
    [Fact]
    public void ForAdministrator_CaseWhitespaceAndCompatibilityVariants_ReturnSameKey()
    {
        var canonical = LoginAccountKey.ForAdministrator("Admin");

        Assert.Equal(canonical, LoginAccountKey.ForAdministrator(" admin "));
        Assert.Equal(canonical, LoginAccountKey.ForAdministrator("ＡＤＭＩＮ"));
    }

    [Fact]
    public void AccountKeys_DifferentAccountScopes_ReturnDifferentKeys()
    {
        var administrator = LoginAccountKey.ForAdministrator("shared-name");
        var firstClient = LoginAccountKey.ForClient(42, "shared-name");
        var secondClient = LoginAccountKey.ForClient(43, "shared-name");

        Assert.NotEqual(administrator, firstClient);
        Assert.NotEqual(firstClient, secondClient);
        Assert.DoesNotContain("SHARED-NAME", administrator, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientSignal_NormalizesMappedIpv4AndDoesNotTrustHeadersDirectly()
    {
        var ipv4 = LoginAccountKey.ClientSignal(IPAddress.Parse("198.51.100.10"));
        var mapped = LoginAccountKey.ClientSignal(IPAddress.Parse("::ffff:198.51.100.10"));

        Assert.Equal("198.51.100.10", ipv4);
        Assert.Equal(ipv4, mapped);
        Assert.Equal("unknown", LoginAccountKey.ClientSignal(null));
    }

    [Fact]
    public void PairKeys_IncludeBothAccountAndClientSignal()
    {
        var account = LoginAccountKey.ForAdministrator("admin");

        Assert.NotEqual(
            LoginAccountKey.Pair(account, "198.51.100.10"),
            LoginAccountKey.Pair(account, "198.51.100.11"));
        Assert.NotEqual(
            LoginAccountKey.Pair(account, "198.51.100.10"),
            LoginAccountKey.Pair(LoginAccountKey.ForAdministrator("other"), "198.51.100.10"));
    }
}
