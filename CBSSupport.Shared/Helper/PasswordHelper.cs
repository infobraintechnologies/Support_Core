using System.Security.Cryptography;
using System.Text;

namespace CBSSupport.Shared.Helpers;

public static class PasswordHelper
{
    private const int CompanyHashRounds = 3;
    private const int SaltSize = 16;

    /// <summary>
    /// Creates the password representation used by the existing company database:
    /// three rounds of Base64(SHA256(previousHash + salt + pepper)).
    /// </summary>
    public static (string Hash, string Salt) HashPassword(
        string password,
        string pepper)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(pepper);

        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize));
        return (ComputeCompanyHash(password, salt, pepper), salt);
    }

    /// <summary>
    /// Verifies a password against the company password_hash/password_salt fields.
    /// Invalid stored encodings fail closed without leaking authentication details.
    /// </summary>
    public static bool VerifyPassword(
        string password,
        string base64Hash,
        string base64Salt,
        string pepper)
    {
        if (string.IsNullOrEmpty(password)
            || string.IsNullOrWhiteSpace(base64Hash)
            || string.IsNullOrWhiteSpace(base64Salt)
            || string.IsNullOrWhiteSpace(pepper))
        {
            return false;
        }

        try
        {
            var expectedHash = Convert.FromBase64String(
                ComputeCompanyHash(password, base64Salt, pepper));
            var storedHash = Convert.FromBase64String(base64Hash);
            return CryptographicOperations.FixedTimeEquals(expectedHash, storedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ComputeCompanyHash(
        string password,
        string salt,
        string pepper)
    {
        var hash = password;
        for (var round = 0; round < CompanyHashRounds; round++)
        {
            var input = Encoding.UTF8.GetBytes(hash + salt + pepper);
            hash = Convert.ToBase64String(SHA256.HashData(input));
        }

        return hash;
    }
}
