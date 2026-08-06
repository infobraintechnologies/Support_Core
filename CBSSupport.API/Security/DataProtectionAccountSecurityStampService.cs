using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace CBSSupport.API.Security;

public sealed class DataProtectionAccountSecurityStampService : IAccountSecurityStampService
{
    private const string ProtectorPurpose = "CBSSupport.AccountSecurityStamp.v1";
    private readonly IDataProtector _protector;

    public DataProtectionAccountSecurityStampService(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    public byte[] Generate() => RandomNumberGenerator.GetBytes(32);

    public string Create(byte[] persistedStamp)
    {
        ValidateStamp(persistedStamp);
        return _protector.Protect(Convert.ToBase64String(persistedStamp));
    }

    public bool Matches(string candidate, byte[] persistedStamp)
    {
        if (string.IsNullOrWhiteSpace(candidate) || persistedStamp.Length != 32)
        {
            return false;
        }

        try
        {
            var protectedFingerprint = Convert.FromBase64String(_protector.Unprotect(candidate));
            return protectedFingerprint.Length == persistedStamp.Length
                && CryptographicOperations.FixedTimeEquals(protectedFingerprint, persistedStamp);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidateStamp(byte[] persistedStamp)
    {
        ArgumentNullException.ThrowIfNull(persistedStamp);
        if (persistedStamp.Length != 32)
        {
            throw new ArgumentException(
                "Security stamps must contain exactly 32 random bytes.",
                nameof(persistedStamp));
        }
    }
}
