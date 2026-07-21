namespace CBSSupport.API.Security;

public interface IAccountSecurityStampService
{
    string Create(string passwordHash, string passwordSalt);

    bool Matches(string candidate, string passwordHash, string passwordSalt);
}
