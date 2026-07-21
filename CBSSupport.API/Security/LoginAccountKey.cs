using System.Security.Cryptography;
using System.Text;

namespace CBSSupport.API.Security;

public static class LoginAccountKey
{
    public static string ForAdministrator(string username) =>
        Hash($"admin:{Normalize(username)}");

    public static string ForClient(long clientId, string username) =>
        Hash($"client:{clientId}:{Normalize(username)}");

    private static string Normalize(string username) =>
        username.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
