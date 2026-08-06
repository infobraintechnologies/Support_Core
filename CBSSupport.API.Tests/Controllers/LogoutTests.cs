using System.Reflection;
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

public sealed class LogoutTests
{
    [Fact]
    public void Logout_Contract_IsPostOnlyAndRequiresAntiforgeryValidation()
    {
        var action = typeof(LoginController).GetMethod(
            nameof(LoginController.Logout),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        var member = Assert.IsAssignableFrom<MemberInfo>(action);
        Assert.NotNull(member.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(member.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.Null(member.GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public async Task Logout_ValidPost_SignsOutCookieAndRedirectsToLogin()
    {
        var authentication = new RecordingAuthenticationService();
        using var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var controller = new LoginController(
            new StubAuthService(),
            new AllowAllLoginAttemptLimiter(),
            new FakeAccountSecurityStampService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services
                }
            }
        };
        controller.Url = new StubUrlHelper(controller.ControllerContext);

        var result = await controller.Logout();

        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, authentication.SignOutScheme);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Login", redirect.ControllerName);
    }

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
        public Task<AdminUser?> ValidateUserAsync(string username, string password) =>
            Task.FromResult<AdminUser?>(null);

        public Task<ClientUser?> ValidateClientUserAsync(long clientCode, string username, string password) =>
            Task.FromResult<ClientUser?>(null);

        public Task<AdminUserDto?> GetAdminUserByIdAsync(long userId) =>
            Task.FromResult<AdminUserDto?>(null);
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public string? SignOutScheme { get; private set; }

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
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            SignOutScheme = scheme;
            return Task.CompletedTask;
        }
    }

    private sealed class StubUrlHelper(ActionContext actionContext) : IUrlHelper
    {
        public ActionContext ActionContext { get; } = actionContext;

        public string? Action(UrlActionContext actionContext) => "/Login/Index";

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => true;

        public string? Link(string? routeName, object? values) => null;

        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }
}
