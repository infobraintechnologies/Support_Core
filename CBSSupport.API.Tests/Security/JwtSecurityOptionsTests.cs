using CBSSupport.API.Security;

namespace CBSSupport.API.Tests.Security;

public sealed class JwtSecurityOptionsTests
{
    [Fact]
    public void Validate_DisabledWithoutSigningConfiguration_DoesNotThrow()
    {
        var options = new JwtSecurityOptions();

        options.Validate();
    }

    [Fact]
    public void Validate_EnabledWithoutSigningConfiguration_Throws()
    {
        var options = new JwtSecurityOptions { Enabled = true };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_EnabledWithValidConfiguration_DoesNotThrow()
    {
        var options = new JwtSecurityOptions
        {
            Enabled = true,
            Key = "test-signing-key-test-signing-key",
            Issuer = "test-issuer",
            Audience = "test-audience",
            AccessTokenLifetime = TimeSpan.FromMinutes(15)
        };

        options.Validate();
    }
}
