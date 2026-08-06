using CBSSupport.API.Security;
using Microsoft.AspNetCore.DataProtection;

namespace CBSSupport.API.Tests.Security;

public sealed class DataProtectionAccountSecurityStampServiceTests
{
    [Fact]
    public void Stamp_RandomPersistedValue_IsProtectedAndOnlyMatchesCurrentValue()
    {
        var service = new DataProtectionAccountSecurityStampService(
            new EphemeralDataProtectionProvider());
        var persistedStamp = Enumerable.Repeat((byte)7, 32).ToArray();
        var replacementStamp = Enumerable.Repeat((byte)8, 32).ToArray();

        var stamp = service.Create(persistedStamp);

        Assert.True(service.Matches(stamp, persistedStamp));
        Assert.False(service.Matches(stamp, replacementStamp));
        Assert.False(service.Matches("not-protected", persistedStamp));
    }

    [Fact]
    public void Generate_ProducesCryptographicallyRandomSizedStamps()
    {
        var service = new DataProtectionAccountSecurityStampService(
            new EphemeralDataProtectionProvider());

        var first = service.Generate();
        var second = service.Generate();

        Assert.Equal(32, first.Length);
        Assert.Equal(32, second.Length);
        Assert.False(first.SequenceEqual(second));
    }
}
