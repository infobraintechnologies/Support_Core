using CBSSupport.API.Security;
using System.Security.Cryptography;

namespace CBSSupport.API.Tests.TestDoubles;

internal sealed class FakeAccountSecurityStampService : IAccountSecurityStampService
{
    public byte[] Generate() => RandomNumberGenerator.GetBytes(32);

    public string Create(byte[] persistedStamp) =>
        $"test-stamp:{Convert.ToBase64String(persistedStamp)}";

    public bool Matches(string candidate, byte[] persistedStamp) =>
        string.Equals(candidate, Create(persistedStamp), StringComparison.Ordinal);
}
