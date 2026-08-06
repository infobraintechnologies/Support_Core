using System.Reflection;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.Shared.Models;
using Microsoft.AspNetCore.RateLimiting;

namespace CBSSupport.API.Tests.Controllers;

public sealed class LoginRateLimitingPolicyTests
{
    [Fact]
    public void MvcLoginPost_DoesNotUseProcessLocalRateLimiter()
    {
        var action = typeof(LoginController).GetMethod(
            nameof(LoginController.Index),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(LoginViewModel)],
            modifiers: null);

        Assert.Empty(
            Assert.IsAssignableFrom<MemberInfo>(action)
                .GetCustomAttributes<EnableRateLimitingAttribute>());
    }

    [Fact]
    public void JwtTokenPost_DoesNotUseProcessLocalRateLimiter()
    {
        var action = typeof(AuthController).GetMethod(nameof(AuthController.GetToken));

        Assert.Empty(
            Assert.IsAssignableFrom<MemberInfo>(action)
                .GetCustomAttributes<EnableRateLimitingAttribute>());
    }
}
