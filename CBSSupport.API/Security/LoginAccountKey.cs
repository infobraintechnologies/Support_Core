using System.Security.Cryptography;
using System.Text;
using System.Net;

namespace CBSSupport.API.Security;

public static class LoginAccountKey
{
    public static string ForAdministrator(string username) =>
        Hash($"admin:{Normalize(username)}");

    public static string ForClient(long clientId, string username) =>
        Hash($"client:{clientId}:{Normalize(username)}");

    public static string ClientSignal(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        var normalized = address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;
        return normalized.ToString();
    }

    public static string Pair(string accountKey, string clientSignal) =>
        Hash($"pair:{accountKey}:{clientSignal}");

    public static string Source(string clientSignal) =>
        Hash($"source:{clientSignal}");

    private static string Normalize(string username) =>
        username.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
