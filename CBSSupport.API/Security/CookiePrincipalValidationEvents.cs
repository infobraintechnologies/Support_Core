using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CBSSupport.API.Security;

public sealed class CookiePrincipalValidationEvents(
    IAccountPrincipalValidator principalValidator,
    TimeProvider timeProvider,
    ILogger<CookiePrincipalValidationEvents> logger) : CookieAuthenticationEvents
{
    public const string LastValidatedUtcProperty = ".account.lastValidatedUtc";
    private static readonly TimeSpan ValidationInterval = TimeSpan.FromMinutes(5);

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var now = timeProvider.GetUtcNow();
        if (!RequiresValidation(context.Properties, now))
        {
            return;
        }

        if (!await principalValidator.ValidateAsync(
                context.Principal,
                context.HttpContext.RequestAborted))
        {
            await RejectAsync(context);
            return;
        }

        context.Properties.Items[LastValidatedUtcProperty] =
            now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        context.ShouldRenew = true;
    }

    private async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        var userId = context.Principal is not null
            && context.Principal.TryGetUserId(out var parsedUserId)
                ? parsedUserId
                : (long?)null;
        logger.LogWarning(
            "Authentication cookie rejected during account revalidation for user {UserId}",
            userId);
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static bool RequiresValidation(AuthenticationProperties properties, DateTimeOffset now)
    {
        if (!properties.Items.TryGetValue(LastValidatedUtcProperty, out var lastValidatedValue)
            || !long.TryParse(
                lastValidatedValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var lastValidatedSeconds))
        {
            return true;
        }

        DateTimeOffset lastValidated;
        try
        {
            lastValidated = DateTimeOffset.FromUnixTimeSeconds(lastValidatedSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }

        return lastValidated > now || now - lastValidated >= ValidationInterval;
    }

}
