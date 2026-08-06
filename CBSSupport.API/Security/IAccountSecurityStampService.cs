namespace CBSSupport.API.Security;

public interface IAccountSecurityStampService
{
    byte[] Generate();

    string Create(byte[] persistedStamp);

    bool Matches(string candidate, byte[] persistedStamp);
}
