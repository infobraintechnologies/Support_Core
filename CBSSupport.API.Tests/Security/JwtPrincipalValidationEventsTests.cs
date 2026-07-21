using System.Security.Claims;
using CBSSupport.API.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;

namespace CBSSupport.API.Tests.Security;

public sealed class JwtPrincipalValidationEventsTests
{
    [Fact]
    public async Task TokenValidated_RevokedAccount_FailsBearerAuthentication()
    {
        var validator = new StubPrincipalValidator(false);
        var events = new JwtPrincipalValidationEvents(validator);
        var context = CreateTokenValidatedContext();

        await events.TokenValidated(context);

        Assert.NotNull(context.Result?.Failure);
        Assert.Equal(1, validator.Calls);
    }

    [Fact]
    public async Task TokenValidated_CurrentAccount_AllowsBearerAuthentication()
    {
        var validator = new StubPrincipalValidator(true);
        var events = new JwtPrincipalValidationEvents(validator);
        var context = CreateTokenValidatedContext();

        await events.TokenValidated(context);

        Assert.Null(context.Result);
        Assert.Equal(1, validator.Calls);
    }

    [Fact]
    public async Task MessageReceived_HubQueryToken_AssignsBearerToken()
    {
        var events = new JwtPrincipalValidationEvents(new StubPrincipalValidator(true));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/chathub";
        httpContext.Request.QueryString = new QueryString("?access_token=test-token");
        var context = new MessageReceivedContext(
            httpContext,
            CreateScheme(),
            new JwtBearerOptions());

        await events.MessageReceived(context);

        Assert.Equal("test-token", context.Token);
    }

    private static TokenValidatedContext CreateTokenValidatedContext()
    {
        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            CreateScheme(),
            new JwtBearerOptions())
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(JwtClaimTypes.Subject, "7")],
                JwtBearerDefaults.AuthenticationScheme))
        };
        return context;
    }

    private static AuthenticationScheme CreateScheme() =>
        new(
            JwtBearerDefaults.AuthenticationScheme,
            null,
            typeof(JwtBearerHandler));

    private sealed class StubPrincipalValidator(bool isValid) : IAccountPrincipalValidator
    {
        public int Calls { get; private set; }

        public Task<bool> ValidateAsync(
            ClaimsPrincipal? principal,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(isValid);
        }
    }
}
