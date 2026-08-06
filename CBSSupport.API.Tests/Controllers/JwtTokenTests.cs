using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.API.Tests.TestDoubles;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Controllers;

public sealed class JwtTokenTests
{
    private const string SigningKey = "test-signing-key-test-signing-key";

    [Fact]
    public async Task GetToken_JwtDisabled_ReturnsNotFoundWithoutValidatingCredentials()
    {
        var authService = new StubAuthService();
        var controller = CreateController(authService, new JwtSecurityOptions());

        var result = await controller.GetToken(CreateLoginModel());

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, authService.AdminValidationCalls);
    }

    [Fact]
    public async Task GetToken_JwtEnabled_EmitsCanonicalAdminClaimsAcceptedByApplication()
    {
        var user = new AdminUser
        {
            Id = 7,
            Username = "admin",
            FullName = "Support Administrator",
            PasswordHash = "password-hash",
            PasswordSalt = "password-salt",
            SecurityStamp = Enumerable.Repeat((byte)7, 32).ToArray()
        };
        var authService = new StubAuthService { AdminUser = user };
        var options = CreateEnabledOptions();
        var controller = CreateController(authService, options);

        var result = await controller.GetToken(CreateLoginModel());

        var ok = Assert.IsType<OkObjectResult>(result);
        var token = Assert.IsType<string>(
            ok.Value?.GetType().GetProperty("token", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(ok.Value));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(user.Id.ToString(CultureInfo.InvariantCulture), jwt.Claims.Single(c => c.Type == JwtClaimTypes.Subject).Value);
        Assert.Equal(user.Username, jwt.Claims.Single(c => c.Type == JwtClaimTypes.Name).Value);
        Assert.Equal(Roles.Admin, jwt.Claims.Single(c => c.Type == JwtClaimTypes.Role).Value);
        Assert.True(new FakeAccountSecurityStampService().Matches(
            jwt.Claims.Single(c => c.Type == CustomClaimTypes.SecurityStamp).Value,
            user.SecurityStamp));
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == "UserId");
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == CustomClaimTypes.ClientId);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == CustomClaimTypes.LegacyClientId);

        var principal = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        }.ValidateToken(
            token,
            CreateValidationParameters(options),
            out _);

        Assert.Equal(user.Username, principal.Identity?.Name);
        Assert.True(principal.IsInRole(Roles.Admin));
        Assert.True(principal.TryGetUserId(out var userId));
        Assert.Equal(user.Id, userId);
        Assert.False(principal.TryGetClientId(out _));
    }

    [Fact]
    public async Task GetMe_RawJwtSubject_UsesCanonicalUserIdentifier()
    {
        var authService = new StubAuthService
        {
            AdminUserDto = new AdminUserDto { Id = 7, Name = "Support Administrator" }
        };
        var chatService = DispatchProxy.Create<IChatService, RecordingChatServiceProxy>();
        var identity = new ClaimsIdentity(
            [
                new Claim(JwtClaimTypes.Subject, "7"),
                new Claim(JwtClaimTypes.Name, "admin"),
                new Claim(JwtClaimTypes.Role, Roles.Admin)
            ],
            "Bearer",
            JwtClaimTypes.Name,
            JwtClaimTypes.Role);
        var controller = new AdminSupportController(
            authService,
            chatService,
            NullLogger<AdminSupportController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity)
                }
            }
        };

        var result = await controller.GetMe();

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(7L, authService.RequestedAdminUserId);
    }

    [Fact]
    public void AdminSupportController_UsesCentralAdminPolicy()
    {
        var attribute = Assert.Single(
            typeof(AdminSupportController).GetCustomAttributes<AuthorizeAttribute>());

        Assert.Equal(Policies.AdminOnly, attribute.Policy);
        Assert.Null(attribute.AuthenticationSchemes);
    }

    private static AuthController CreateController(
        StubAuthService authService,
        JwtSecurityOptions options) =>
        new(
            authService,
            new AllowAllLoginAttemptLimiter(),
            options,
            TimeProvider.System,
            new FakeAccountSecurityStampService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

    private static LoginViewModel CreateLoginModel() =>
        new()
        {
            RoleType = "admin",
            Username = "admin",
            Password = "password"
        };

    private static JwtSecurityOptions CreateEnabledOptions() =>
        new()
        {
            Enabled = true,
            Key = SigningKey,
            Issuer = "test-issuer",
            Audience = "test-audience",
            AccessTokenLifetime = TimeSpan.FromMinutes(15)
        };

    private static TokenValidationParameters CreateValidationParameters(JwtSecurityOptions options) =>
        new()
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(options.Key!)),
            NameClaimType = JwtClaimTypes.Name,
            RoleClaimType = JwtClaimTypes.Role,
            ClockSkew = TimeSpan.FromMinutes(1)
        };

    private sealed class AllowAllLoginAttemptLimiter : ILoginAttemptLimiter
    {
        public Task<LoginAttemptDecision> CheckAsync(
            string accountKey,
            string clientSignal,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(LoginAttemptDecision.Allowed);

        public Task RecordFailureAsync(
            string accountKey,
            string clientSignal,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetAsync(
            string accountKey,
            string clientSignal,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubAuthService : IAuthService
    {
        public AdminUser? AdminUser { get; init; }
        public AdminUserDto? AdminUserDto { get; init; }
        public int AdminValidationCalls { get; private set; }
        public long? RequestedAdminUserId { get; private set; }

        public Task<AdminUser?> ValidateUserAsync(string username, string password)
        {
            AdminValidationCalls++;
            return Task.FromResult(AdminUser);
        }

        public Task<ClientUser?> ValidateClientUserAsync(long clientCode, string username, string password) =>
            Task.FromResult<ClientUser?>(null);

        public Task<AdminUserDto?> GetAdminUserByIdAsync(long userId)
        {
            RequestedAdminUserId = userId;
            return Task.FromResult(AdminUserDto);
        }
    }
}
