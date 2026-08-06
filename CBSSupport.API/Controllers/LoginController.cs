using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using CBSSupport.API.Security;

namespace CBSSupport.API.Controllers
{
    [AllowAnonymous]
    public class LoginController : Controller
    {
        private readonly IAuthService _authService;
        private readonly ILoginAttemptLimiter _loginAttemptLimiter;
        private readonly IAccountSecurityStampService _securityStamps;

        public LoginController(
            IAuthService authService,
            ILoginAttemptLimiter loginAttemptLimiter,
            IAccountSecurityStampService securityStamps)
        {
            _authService = authService;
            _loginAttemptLimiter = loginAttemptLimiter;
            _securityStamps = securityStamps;
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
        [EnableRateLimiting(LoginRateLimitPolicies.PerIp)]
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
                if (!TryAcquireAccountAttempt(accountKey))
                {
                    return View(model);
                }

                var adminUser = await _authService.ValidateUserAsync(model.Username, model.Password);
                if (adminUser != null)
                {
                    _loginAttemptLimiter.Reset(accountKey);

                    var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, adminUser.Id.ToString()),
                    new Claim(ClaimTypes.Name, adminUser.Username),
                        new Claim(ClaimTypes.Role, Roles.Admin),
                        new Claim(CustomClaimTypes.AdminTenantAccess, "*"),
                    new Claim("FullName", adminUser.FullName),
                        new Claim(
                            CustomClaimTypes.SecurityStamp,
                            _securityStamps.Create(adminUser.PasswordHash, adminUser.PasswordSalt))
                };

                    await SignInUser(claims, model.RememberMe, "/AdminSupport");

                    return RedirectToAction("Index", "AdminSupport");
                }

                _loginAttemptLimiter.RecordFailure(accountKey);
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
                if (!TryAcquireAccountAttempt(accountKey))
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
                    _loginAttemptLimiter.Reset(accountKey);

                    var claims = new List<Claim>
                    {
                        new Claim(ClaimTypes.NameIdentifier, clientUser.Id.ToString()),
                        new Claim(ClaimTypes.Name, clientUser.Username),
                        new Claim(ClaimTypes.Role, Roles.Client),
                        new Claim("FullName", clientUser.FullName),
                        new Claim(CustomClaimTypes.ClientId, clientUser.ClientId.ToString()),
                        new Claim(
                            CustomClaimTypes.SecurityStamp,
                            _securityStamps.Create(clientUser.PasswordHash, clientUser.PasswordSalt))
                    };

                    await SignInUser(claims, model.RememberMe, "/Support");

                    return RedirectToAction("Index", "Support");
                }

                _loginAttemptLimiter.RecordFailure(accountKey);
            }

            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return View(model);
        }

        private bool TryAcquireAccountAttempt(string accountKey)
        {
            var decision = _loginAttemptLimiter.Check(accountKey);
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
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Login");
        }
    }
}
