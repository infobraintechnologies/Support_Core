using CBSSupport.API.Security;
using CBSSupport.Shared.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Controllers;

[ApiController]
[Route("api/v1/session")]
[Authorize(Policy = Policies.AdminOrClient)]
public sealed class SessionController(
    IAccountSecurityStampRotationService securityStamps) : ControllerBase
{
    [HttpPost("revoke-all")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeAll(
        CancellationToken cancellationToken)
    {
        if (!TryGetAccountReference(out var account))
        {
            return Unauthorized();
        }

        if (!await securityStamps.RevokeAllSessionsAsync(account, cancellationToken))
        {
            return NotFound();
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    private bool TryGetAccountReference(out AccountReference account)
    {
        account = default;
        if (!User.TryGetUserId(out var userId))
        {
            return false;
        }

        if (User.IsInRole(Roles.Client) && !User.IsInRole(Roles.Admin))
        {
            account = new AccountReference(AccountKind.Client, userId);
            return true;
        }

        if (User.IsInRole(Roles.Admin) && !User.IsInRole(Roles.Client))
        {
            account = new AccountReference(AccountKind.Administrator, userId);
            return true;
        }

        return false;
    }
}
