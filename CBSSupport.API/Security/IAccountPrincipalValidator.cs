using System.Security.Claims;

namespace CBSSupport.API.Security;

public interface IAccountPrincipalValidator
{
    Task<bool> ValidateAsync(
        ClaimsPrincipal? principal,
        CancellationToken cancellationToken = default);
}
