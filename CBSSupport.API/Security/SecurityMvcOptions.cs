using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Security;

public static class SecurityMvcOptions
{
    public static void ConfigureMvc(MvcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Filters.Add<CookieAntiforgeryFilter>();
    }

    public static void ConfigureAntiforgery(AntiforgeryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.HeaderName = AntiforgeryConstants.HeaderName;
    }
}
