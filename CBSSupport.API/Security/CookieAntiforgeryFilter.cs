using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CBSSupport.API.Security;

public sealed class CookieAntiforgeryFilter(
    IAntiforgery antiforgery,
    ILogger<CookieAntiforgeryFilter> logger) : IAsyncAuthorizationFilter, IOrderedFilter
{
    public int Order => 1000;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (HttpMethods.IsGet(context.HttpContext.Request.Method)
            || HttpMethods.IsHead(context.HttpContext.Request.Method)
            || HttpMethods.IsOptions(context.HttpContext.Request.Method)
            || HttpMethods.IsTrace(context.HttpContext.Request.Method)
            || context.ActionDescriptor.EndpointMetadata.OfType<IgnoreAntiforgeryTokenAttribute>().Any()
            || !IsCookieAuthenticated(context.HttpContext.User))
        {
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            logger.LogWarning(
                "Cookie-authenticated unsafe request rejected by antiforgery validation for {Method} {Path}",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path);
            context.Result = new AntiforgeryValidationFailedResult();
        }
    }

    private static bool IsCookieAuthenticated(System.Security.Claims.ClaimsPrincipal principal) =>
        principal.Identities.Any(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                CookieAuthenticationDefaults.AuthenticationScheme,
                StringComparison.Ordinal));
}
