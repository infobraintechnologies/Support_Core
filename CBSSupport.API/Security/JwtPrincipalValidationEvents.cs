using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace CBSSupport.API.Security;

public sealed class JwtPrincipalValidationEvents(
    IAccountPrincipalValidator principalValidator) : JwtBearerEvents
{
    public override Task MessageReceived(MessageReceivedContext context)
    {
        var accessToken = context.Request.Query["access_token"];
        if (!string.IsNullOrEmpty(accessToken)
            && context.HttpContext.Request.Path.StartsWithSegments("/chathub"))
        {
            context.Token = accessToken;
        }

        return Task.CompletedTask;
    }

    public override async Task TokenValidated(TokenValidatedContext context)
    {
        if (!await principalValidator.ValidateAsync(
                context.Principal,
                context.HttpContext.RequestAborted))
        {
            context.Fail("The bearer token is no longer valid.");
        }
    }
}
