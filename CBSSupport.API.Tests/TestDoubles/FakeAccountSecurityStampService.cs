using CBSSupport.API.Security;

namespace CBSSupport.API.Tests.TestDoubles;

internal sealed class FakeAccountSecurityStampService : IAccountSecurityStampService
{
    public string Create(string passwordHash, string passwordSalt) =>
        $"test-stamp:{passwordHash.Length}:{passwordHash}:{passwordSalt}";

    public bool Matches(string candidate, string passwordHash, string passwordSalt) =>
        string.Equals(candidate, Create(passwordHash, passwordSalt), StringComparison.Ordinal);
}
