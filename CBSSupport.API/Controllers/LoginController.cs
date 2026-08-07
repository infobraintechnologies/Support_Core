using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using CBSSupport.API.Security;
using CBSSupport.Shared.Data;

namespace CBSSupport.API.Controllers
{
    [AllowAnonymous]
    public class LoginController : Controller
    {
        private readonly IAuthService _authService;
        private readonly ILoginAttemptLimiter _loginAttemptLimiter;
        private readonly IAccountSecurityStampService _securityStamps;
        private readonly ISecurityAuditWriter _securityAudit;

        public LoginController(
            IAuthService authService,
            ILoginAttemptLimiter loginAttemptLimiter,
            IAccountSecurityStampService securityStamps,
            ISecurityAuditWriter? securityAudit = null)
        {
            _authService = authService;
            _loginAttemptLimiter = loginAttemptLimiter;
            _securityStamps = securityStamps;
            _securityAudit = securityAudit ?? new NullSecurityAuditWriter();
        }

        [HttpGet]
        public IActionResult Index()
        {
            if (User.Identity is { IsAuthenticated: true })
            {
                if (User.IsInRole(Roles.Client))
                {
                    return RedirectToAction("Index", "Support");
                }
                return RedirectToAction("Index", "AdminSupport");
            }
            return View(new LoginViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(LoginViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            if (model.RoleType == "admin")
            {
                if (string.IsNullOrEmpty(model.Username) || string.IsNullOrEmpty(model.Password))
                {
                    ModelState.AddModelError(string.Empty, "Username and Password are required for admin login.");
                    return View(model);
                }

                var accountKey = LoginAccountKey.ForAdministrator(model.Username);
                var clientSignal = LoginAccountKey.ClientSignal(HttpContext.Connection.RemoteIpAddress);
                if (!await TryAcquireAccountAttemptAsync(accountKey, clientSignal, "admin"))
                {
                    return View(model);
                }

                var adminUser = await _authService.ValidateUserAsync(model.Username, model.Password);
                if (adminUser != null)
                {
                    await _loginAttemptLimiter.ResetAsync(
                        accountKey,
                        clientSignal,
                        HttpContext.RequestAborted);

                    var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, adminUser.Id.ToString()),
                    new Claim(ClaimTypes.Name, adminUser.Username),
                        new Claim(ClaimTypes.Role, Roles.Admin),
                        new Claim(CustomClaimTypes.AdminTenantAccess, "*"),
                    new Claim("FullName", adminUser.FullName),
                        new Claim(
                            CustomClaimTypes.SecurityStamp,
                            _securityStamps.Create(adminUser.SecurityStamp))
                };

                    await SignInUser(claims, model.RememberMe, "/AdminSupport");

                    await _securityAudit.AppendAsync(
                        SecurityAuditContext.ForHttpRequest(
                            HttpContext,
                            "AuthenticationSucceeded",
                            SecurityAuditOutcomes.Success,
                            targetKind: "Account",
                            targetId: adminUser.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            details: new Dictionary<string, string?> { ["role"] = Roles.Admin }));

                    return RedirectToAction("Index", "AdminSupport");
                }

                await _loginAttemptLimiter.RecordFailureAsync(
                    accountKey,
                    clientSignal,
                    HttpContext.RequestAborted);
                await _securityAudit.AppendAsync(
                    SecurityAuditContext.ForHttpRequest(
                        HttpContext,
                        "AuthenticationFailed",
                        SecurityAuditOutcomes.Failure,
                        targetKind: "Authentication",
                        targetId: "admin",
                        details: new Dictionary<string, string?> { ["role"] = Roles.Admin }));
            }
            else if (model.RoleType == "client" && model.ClientLogin != null)
            {

                if (!model.ClientLogin.ClientCode.HasValue || string.IsNullOrEmpty(model.ClientLogin.Username) || string.IsNullOrEmpty(model.ClientLogin.Password))
                {
                    ModelState.AddModelError(string.Empty, "Client Code, Username, and Password are required.");
                    return View(model);
                }

                var accountKey = LoginAccountKey.ForClient(
                    model.ClientLogin.ClientCode.Value,
                    model.ClientLogin.Username);
                var clientSignal = LoginAccountKey.ClientSignal(HttpContext.Connection.RemoteIpAddress);
                if (!await TryAcquireAccountAttemptAsync(accountKey, clientSignal, "client"))
                {
                    return View(model);
                }

                var clientUser = await _authService.ValidateClientUserAsync(
                    model.ClientLogin.ClientCode.Value,
                    model.ClientLogin.Username,
                    model.ClientLogin.Password
                );

                if (clientUser != null)
                {
                    await _loginAttemptLimiter.ResetAsync(
                        accountKey,
                        clientSignal,
                        HttpContext.RequestAborted);

                    var claims = new List<Claim>
                    {
                        new Claim(ClaimTypes.NameIdentifier, clientUser.Id.ToString()),
                        new Claim(ClaimTypes.Name, clientUser.Username),
                        new Claim(ClaimTypes.Role, Roles.Client),
                        new Claim("FullName", clientUser.FullName),
                        new Claim(CustomClaimTypes.ClientId, clientUser.ClientId.ToString()),
                        new Claim(
                            CustomClaimTypes.SecurityStamp,
                            _securityStamps.Create(clientUser.SecurityStamp))
                    };

                    await SignInUser(claims, model.RememberMe, "/Support");

                    await _securityAudit.AppendAsync(
                        SecurityAuditContext.ForHttpRequest(
                            HttpContext,
                            "AuthenticationSucceeded",
                            SecurityAuditOutcomes.Success,
                            clientUser.ClientId,
                            "Account",
                            clientUser.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            new Dictionary<string, string?> { ["role"] = Roles.Client }));

                    return RedirectToAction("Index", "Support");
                }

                await _loginAttemptLimiter.RecordFailureAsync(
                    accountKey,
                    clientSignal,
                    HttpContext.RequestAborted);
                await _securityAudit.AppendAsync(
                    SecurityAuditContext.ForHttpRequest(
                        HttpContext,
                        "AuthenticationFailed",
                        SecurityAuditOutcomes.Failure,
                        targetKind: "Authentication",
                        targetId: "client",
                        details: new Dictionary<string, string?> { ["role"] = Roles.Client }));
            }

            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return View(model);
        }

        private async Task<bool> TryAcquireAccountAttemptAsync(
            string accountKey,
            string clientSignal,
            string role)
        {
            var decision = await _loginAttemptLimiter.CheckAsync(
                accountKey,
                clientSignal,
                HttpContext.RequestAborted);
            if (decision.IsAllowed)
            {
                return true;
            }

            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            ModelState.AddModelError(
                string.Empty,
                "Too many login attempts. Wait before trying again.");
            await _securityAudit.AppendAsync(
                SecurityAuditContext.ForHttpRequest(
                    HttpContext,
                    "AuthenticationThrottled",
                    SecurityAuditOutcomes.Throttled,
                    targetKind: "Authentication",
                    targetId: role,
                    details: new Dictionary<string, string?>
                    {
                        ["role"] = role,
                        ["reason"] = decision.BlockReason.ToString()
                    }));
            return false;
        }

        private async Task SignInUser(List<Claim> claims, bool isPersistent, string redirectUri)
        {
            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = isPersistent,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(60),
                RedirectUri = redirectUri
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _securityAudit.AppendAsync(
                SecurityAuditContext.ForHttpRequest(
                    HttpContext,
                    "Logout",
                    SecurityAuditOutcomes.Success));
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Login");
        }
    }
}
