using System.Security.Cryptography;
using System.Text;
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

    public string Create(string passwordHash, string passwordSalt) =>
        _protector.Protect(Convert.ToBase64String(ComputeFingerprint(passwordHash, passwordSalt)));

    public bool Matches(string candidate, string passwordHash, string passwordSalt)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            var protectedFingerprint = Convert.FromBase64String(_protector.Unprotect(candidate));
            var currentFingerprint = ComputeFingerprint(passwordHash, passwordSalt);
            return protectedFingerprint.Length == currentFingerprint.Length
                && CryptographicOperations.FixedTimeEquals(protectedFingerprint, currentFingerprint);
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

    private static byte[] ComputeFingerprint(string passwordHash, string passwordSalt)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);
        ArgumentNullException.ThrowIfNull(passwordSalt);

        var material = $"{passwordHash.Length}:{passwordHash}{passwordSalt.Length}:{passwordSalt}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }
}
