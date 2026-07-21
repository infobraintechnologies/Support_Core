using CBSSupport.API.Security;

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
}
