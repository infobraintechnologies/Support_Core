using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.API.Tests.TestDoubles;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Controllers;

public sealed class LoginRateLimitingTests
{
    [Fact]
    public async Task LoginController_ExhaustedAccountLimiter_Returns429AndSkipsAuthService()
    {
        var limiter = CreateLimiter(failedAttemptsBeforeBackoff: 2);
        var authService = new CountingAuthService();
        var model = new LoginViewModel
        {
            RoleType = "admin",
            Username = "admin",
            Password = "bad"
        };
        await CreateLoginController(limiter, authService).Index(model);
        await CreateLoginController(limiter, authService).Index(model);
        var controller = CreateLoginController(limiter, authService);
        var result = await controller.Index(model);

        Assert.IsType<ViewResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, controller.Response.StatusCode);
        Assert.Equal(2, authService.AdminValidationCalls);
    }

    [Fact]
    public async Task AuthController_ExhaustedAccountLimiter_Returns429AndSkipsAuthService()
    {
        var limiter = CreateLimiter(failedAttemptsBeforeBackoff: 2);
        var authService = new CountingAuthService();
        var model = new LoginViewModel
        {
            RoleType = "admin",
            Username = "admin",
            Password = "bad"
        };
        var controller = CreateAuthController(limiter, authService);

        await controller.GetToken(model);
        await controller.GetToken(model);
        var result = await controller.GetToken(model);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);
        Assert.Equal(2, authService.AdminValidationCalls);
    }

    private static LoginController CreateLoginController(
        ILoginAttemptLimiter limiter,
        CountingAuthService authService)
    {
        return new LoginController(authService, limiter, new FakeAccountSecurityStampService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static AuthController CreateAuthController(
        ILoginAttemptLimiter limiter,
        CountingAuthService authService)
    {
        return new AuthController(
            authService,
            limiter,
            new JwtSecurityOptions
            {
                Enabled = true,
                Key = "test-signing-key-test-signing-key",
                Issuer = "test-issuer",
                Audience = "test-audience"
            },
            TimeProvider.System,
            new FakeAccountSecurityStampService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static LoginAttemptLimiter CreateLimiter(int failedAttemptsBeforeBackoff)
    {
        var options = new LoginSecurityOptions
        {
            PerIpPermitLimit = 10,
            PerIpWindow = TimeSpan.FromMinutes(1),
            PerIpSegments = 1,
            FailedAttemptsBeforeBackoff = failedAttemptsBeforeBackoff,
            InitialBackoff = TimeSpan.FromMinutes(1),
            MaximumBackoff = TimeSpan.FromMinutes(4),
            StateRetention = TimeSpan.FromMinutes(30),
            MaximumTrackedAccounts = 32
        };

        return new LoginAttemptLimiter(options, TimeProvider.System);
    }

    private sealed class CountingAuthService : IAuthService
    {
        public int AdminValidationCalls { get; private set; }

        public Task<AdminUser?> ValidateUserAsync(string username, string password)
        {
            AdminValidationCalls++;
            return Task.FromResult<AdminUser?>(null);
        }

        public Task<ClientUser?> ValidateClientUserAsync(long clientCode, string username, string password) =>
            Task.FromResult<ClientUser?>(null);

        public Task<AdminUserDto?> GetAdminUserByIdAsync(long userId) =>
            Task.FromResult<AdminUserDto?>(null);
    }
}
