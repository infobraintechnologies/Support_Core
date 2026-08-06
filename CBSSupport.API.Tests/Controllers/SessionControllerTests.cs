using System.Reflection;
using System.Security.Claims;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.Shared.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CBSSupport.API.Tests.Controllers;

public sealed class SessionControllerTests
{
    [Fact]
    public void RevokeAll_Contract_IsPostOnlyAndRequiresAntiforgeryValidation()
    {
        var action = typeof(SessionController).GetMethod(
            nameof(SessionController.RevokeAll),
            BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(action);
        Assert.NotNull(action!.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public async Task RevokeAll_Administrator_RotatesOwnStampAndSignsOutCurrentCookie()
    {
        var rotations = new RecordingRotationService();
        var authentication = new RecordingAuthenticationService();
        using var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var controller = CreateController(
            rotations,
            services,
            new Claim(ClaimTypes.NameIdentifier, "7"),
            new Claim(ClaimTypes.Role, Roles.Admin));

        var result = await controller.RevokeAll(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(
            new AccountReference(AccountKind.Administrator, 7),
            rotations.Account);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, authentication.SignOutScheme);
    }

    [Fact]
    public async Task RevokeAll_Client_UsesAuthenticatedUserWithoutCallerSelectedIdentity()
    {
        var rotations = new RecordingRotationService();
        using var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new RecordingAuthenticationService())
            .BuildServiceProvider();
        var controller = CreateController(
            rotations,
            services,
            new Claim(ClaimTypes.NameIdentifier, "11"),
            new Claim(ClaimTypes.Role, Roles.Client),
            new Claim(CustomClaimTypes.ClientId, "42"));

        var result = await controller.RevokeAll(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(new AccountReference(AccountKind.Client, 11), rotations.Account);
    }

    private static SessionController CreateController(
        IAccountSecurityStampRotationService rotations,
        ServiceProvider services,
        params Claim[] claims) =>
        new(rotations)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"))
                }
            }
        };

    private sealed class RecordingRotationService : IAccountSecurityStampRotationService
    {
        public AccountReference? Account { get; private set; }

        public Task<bool> RevokeAllSessionsAsync(
            AccountReference account,
            CancellationToken cancellationToken = default)
        {
            Account = account;
            return Task.FromResult(true);
        }

        public Task<bool> RotateAsync(
            AccountReference account,
            SecurityStampRotationReason reason,
            byte[]? expectedStamp = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RotateForPasswordChangeAsync(
            AccountReference account,
            byte[]? expectedStamp = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RotateForPasswordResetAsync(
            AccountReference account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RotateForRoleChangeAsync(
            AccountReference account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RotateForAccountCompromiseAsync(
            AccountReference account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
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
}
