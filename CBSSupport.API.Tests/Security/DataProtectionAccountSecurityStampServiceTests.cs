using CBSSupport.API.Security;
using Microsoft.AspNetCore.DataProtection;

namespace CBSSupport.API.Tests.Security;

public sealed class DataProtectionAccountSecurityStampServiceTests
{
    [Fact]
    public void Stamp_PasswordCredentialChange_InvalidatesProtectedStampWithoutExposingCredentials()
    {
        const string passwordHash = "stored-password-hash";
        const string passwordSalt = "stored-password-salt";
        var service = new DataProtectionAccountSecurityStampService(
            new EphemeralDataProtectionProvider());

        var stamp = service.Create(passwordHash, passwordSalt);

        Assert.DoesNotContain(passwordHash, stamp, StringComparison.Ordinal);
        Assert.DoesNotContain(passwordSalt, stamp, StringComparison.Ordinal);
        Assert.True(service.Matches(stamp, passwordHash, passwordSalt));
        Assert.False(service.Matches(stamp, "replacement-hash", passwordSalt));
        Assert.False(service.Matches("not-protected", passwordHash, passwordSalt));
    }
}
