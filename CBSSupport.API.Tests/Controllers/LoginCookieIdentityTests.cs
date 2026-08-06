using System.Security.Claims;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.API.Tests.TestDoubles;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CBSSupport.API.Tests.Controllers;

public sealed class LoginCookieIdentityTests
{
    private static readonly IAccountSecurityStampService SecurityStamps =
        new FakeAccountSecurityStampService();

    [Fact]
    public async Task Login_ValidAdministrator_UsesCanonicalClaimsAndCredentialStampWithoutSession()
    {
        var user = new AdminUser
        {
            Id = 7,
            Username = "admin",
            FullName = "Admin User",
            PasswordHash = "password-hash",
            PasswordSalt = "password-salt",
            SecurityStamp = Enumerable.Repeat((byte)7, 32).ToArray(),
            Status = true
        };
        var authentication = new RecordingAuthenticationService();
        var controller = CreateController(
            new StubAuthService { AdminUser = user },
            authentication);

        var result = await controller.Index(new LoginViewModel
        {
            RoleType = "admin",
            Username = user.Username,
            Password = "password"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("AdminSupport", redirect.ControllerName);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, authentication.SignInScheme);
        Assert.Equal(user.Id.ToString(), authentication.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(Roles.Admin, authentication.Principal?.FindFirstValue(ClaimTypes.Role));
        Assert.True(SecurityStamps.Matches(
            authentication.Principal!.FindFirstValue(CustomClaimTypes.SecurityStamp)!,
            user.SecurityStamp));
    }

    [Fact]
    public async Task Login_ValidClient_UsesCanonicalUserAndTenantClaimsWithoutSession()
    {
        var user = new ClientUser
        {
            Id = 11,
            ClientId = 42,
            Username = "client",
            FullName = "Client User",
            PasswordHash = "password-hash",
            PasswordSalt = "password-salt",
            SecurityStamp = Enumerable.Repeat((byte)8, 32).ToArray(),
            Status = true
        };
        var authentication = new RecordingAuthenticationService();
        var controller = CreateController(
            new StubAuthService { ClientUser = user },
            authentication);

        var result = await controller.Index(new LoginViewModel
        {
            RoleType = "client",
            ClientLogin = new ClientLoginViewModel
            {
                ClientCode = user.ClientId,
                Username = user.Username,
                Password = "password"
            }
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Support", redirect.ControllerName);
        Assert.Equal(user.Id.ToString(), authentication.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(user.ClientId.ToString(), authentication.Principal?.FindFirstValue(CustomClaimTypes.ClientId));
        Assert.DoesNotContain(authentication.Principal!.Claims, claim => claim.Type == "UserId");
        Assert.DoesNotContain(authentication.Principal.Claims, claim => claim.Type == CustomClaimTypes.LegacyClientId);
    }

    private static LoginController CreateController(
        IAuthService authService,
        RecordingAuthenticationService authentication)
    {
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var controller = new LoginController(
            authService,
            new AllowAllLoginAttemptLimiter(),
            SecurityStamps)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services }
            }
        };
        controller.Url = new StubUrlHelper(controller.ControllerContext);
        return controller;
    }

    private sealed class StubAuthService : IAuthService
    {
        public AdminUser? AdminUser { get; init; }
        public ClientUser? ClientUser { get; init; }

        public Task<AdminUser?> ValidateUserAsync(string username, string password) =>
            Task.FromResult(AdminUser);

        public Task<ClientUser?> ValidateClientUserAsync(
            long clientCode,
            string username,
            string password) => Task.FromResult(ClientUser);

        public Task<AdminUserDto?> GetAdminUserByIdAsync(long userId) =>
            Task.FromResult<AdminUserDto?>(null);
    }

    private sealed class AllowAllLoginAttemptLimiter : ILoginAttemptLimiter
    {
        public LoginAttemptDecision Check(string accountKey) => LoginAttemptDecision.Allowed;

        public void RecordFailure(string accountKey)
        {
        }

        public void Reset(string accountKey)
        {
        }
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public string? SignInScheme { get; private set; }
        public ClaimsPrincipal? Principal { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties)
        {
            SignInScheme = scheme;
            Principal = principal;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class StubUrlHelper(ActionContext actionContext) : IUrlHelper
    {
        public ActionContext ActionContext { get; } = actionContext;

        public string? Action(UrlActionContext actionContext) => "/";

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => true;

        public string? Link(string? routeName, object? values) => null;

        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }
}
