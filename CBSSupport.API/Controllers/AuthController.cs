using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Globalization;
using System.Text;
using CBSSupport.API.Security;
using CBSSupport.Shared.Data;

namespace CBSSupport.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly ILoginAttemptLimiter _loginAttemptLimiter;
        private readonly JwtSecurityOptions _jwtOptions;
        private readonly TimeProvider _timeProvider;
        private readonly IAccountSecurityStampService _securityStamps;
        private readonly ISecurityAuditWriter _securityAudit;

        public AuthController(
            IAuthService authService,
            ILoginAttemptLimiter loginAttemptLimiter,
            JwtSecurityOptions jwtOptions,
            TimeProvider timeProvider,
            IAccountSecurityStampService securityStamps,
            ISecurityAuditWriter? securityAudit = null)
        {
            _authService = authService;
            _loginAttemptLimiter = loginAttemptLimiter;
            _jwtOptions = jwtOptions;
            _timeProvider = timeProvider;
            _securityStamps = securityStamps;
            _securityAudit = securityAudit ?? new NullSecurityAuditWriter();
        }

        [HttpPost("token")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> GetToken([FromBody] LoginViewModel model)
        {
            if (!_jwtOptions.Enabled)
            {
                return NotFound();
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (string.IsNullOrWhiteSpace(model.Username) || string.IsNullOrEmpty(model.Password))
            {
                return BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Invalid login request",
                    Detail = "Username and password are required."
                });
            }

            var accountKey = LoginAccountKey.ForAdministrator(model.Username);
            var clientSignal = LoginAccountKey.ClientSignal(HttpContext.Connection.RemoteIpAddress);
            var decision = await _loginAttemptLimiter.CheckAsync(
                accountKey,
                clientSignal,
                HttpContext.RequestAborted);
            if (!decision.IsAllowed)
            {
                await _securityAudit.AppendAsync(
                    SecurityAuditContext.ForHttpRequest(
                        HttpContext,
                        "AuthenticationThrottled",
                        SecurityAuditOutcomes.Throttled,
                        targetKind: "Authentication",
                        targetId: "admin",
                        details: new Dictionary<string, string?>
                        {
                            ["role"] = Roles.Admin,
                            ["reason"] = decision.BlockReason.ToString()
                        }));
                Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many login attempts",
                    detail: "Wait before trying to sign in again.");
            }

            var user = await _authService.ValidateUserAsync(model.Username, model.Password);

            if (user != null)
            {
                await _loginAttemptLimiter.ResetAsync(
                    accountKey,
                    clientSignal,
                    HttpContext.RequestAborted);
                var tokenString = GenerateJwtToken(user);
                await _securityAudit.AppendAsync(
                    SecurityAuditContext.ForHttpRequest(
                        HttpContext,
                        "AuthenticationSucceeded",
                        SecurityAuditOutcomes.Success,
                        targetKind: "Account",
                        targetId: user.Id.ToString(CultureInfo.InvariantCulture),
                        details: new Dictionary<string, string?>
                        {
                            ["role"] = Roles.Admin,
                            ["scheme"] = "bearer"
                        }));
                return Ok(new { token = tokenString, message = "Token generated successfully." });
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
                    details: new Dictionary<string, string?>
                    {
                        ["role"] = Roles.Admin,
                        ["scheme"] = "bearer"
                    }));
            return Unauthorized(new { message = "Invalid username or password." });
        }

        private string GenerateJwtToken(AdminUser user)
        {
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Key!));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
            var now = _timeProvider.GetUtcNow();

            var claims = new List<Claim>
            {
                new Claim(JwtClaimTypes.Subject, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(JwtClaimTypes.Name, user.Username),
                new Claim("FullName", user.FullName),
                new Claim(JwtClaimTypes.Role, Roles.Admin),
                new Claim(CustomClaimTypes.AdminTenantAccess, "*"),
                new Claim(
                    CustomClaimTypes.SecurityStamp,
                    _securityStamps.Create(user.SecurityStamp)),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: _jwtOptions.Issuer,
                audience: _jwtOptions.Audience,
                claims: claims,
                notBefore: now.UtcDateTime,
                expires: now.Add(_jwtOptions.AccessTokenLifetime).UtcDateTime,
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
