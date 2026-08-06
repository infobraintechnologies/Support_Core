using CBSSupport.Shared.Helpers;

namespace CBSSupport.API.Tests.Services;

public sealed class PasswordHelperTests
{
    [Fact]
    public void VerifyPassword_CompanyKnownVector_ReturnsTrue()
    {
        const string password = "correct horse battery staple";
        const string salt = "MDEyMzQ1Njc4OWFiY2RlZg==";
        const string pepper = "test-company-pepper";
        const string expectedHash = "h01CCF0Eib5va/Oa8YodN8R1n8CtzEQ++qnrEFtGDAI=";

        Assert.True(PasswordHelper.VerifyPassword(password, expectedHash, salt, pepper));
        Assert.False(PasswordHelper.VerifyPassword("wrong password", expectedHash, salt, pepper));
    }

    [Fact]
    public void HashPassword_GeneratesSixteenByteBase64Salt()
    {
        var (hash, salt) = PasswordHelper.HashPassword("password", "test-company-pepper");

        Assert.Equal(16, Convert.FromBase64String(salt).Length);
        Assert.True(PasswordHelper.VerifyPassword("password", hash, salt, "test-company-pepper"));
    }
}
