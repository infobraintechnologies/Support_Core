using System.Reflection;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.Shared.Models;
using Microsoft.AspNetCore.RateLimiting;

namespace CBSSupport.API.Tests.Controllers;

public sealed class LoginRateLimitingPolicyTests
{
    [Fact]
    public void MvcLoginPost_UsesPerIpRateLimitPolicy()
    {
        var action = typeof(LoginController).GetMethod(
            nameof(LoginController.Index),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(LoginViewModel)],
            modifiers: null);

        var attribute = Assert.Single(
            Assert.IsAssignableFrom<MemberInfo>(action)
                .GetCustomAttributes<EnableRateLimitingAttribute>());

        Assert.Equal(LoginRateLimitPolicies.PerIp, attribute.PolicyName);
    }

    [Fact]
    public void JwtTokenPost_UsesPerIpRateLimitPolicy()
    {
        var action = typeof(AuthController).GetMethod(nameof(AuthController.GetToken));

        var attribute = Assert.Single(
            Assert.IsAssignableFrom<MemberInfo>(action)
                .GetCustomAttributes<EnableRateLimitingAttribute>());

        Assert.Equal(LoginRateLimitPolicies.PerIp, attribute.PolicyName);
    }
}
