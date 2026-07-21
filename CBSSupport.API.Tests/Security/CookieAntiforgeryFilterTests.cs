using System.Security.Claims;
using CBSSupport.API.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Security;

public sealed class CookieAntiforgeryFilterTests
{
    [Fact]
    public async Task UnsafeCookieRequest_InvalidToken_ReturnsAntiforgeryBadRequest()
    {
        var antiforgery = new StubAntiforgery { IsValid = false };
        var filter = CreateFilter(antiforgery);
        var context = CreateContext(HttpMethods.Post, CookieAuthenticationDefaults.AuthenticationScheme);

        await filter.OnAuthorizationAsync(context);

        Assert.IsType<AntiforgeryValidationFailedResult>(context.Result);
        Assert.Equal(1, antiforgery.ValidationCalls);
    }

    [Fact]
    public async Task UnsafeCookieRequest_ValidToken_AllowsAction()
    {
        var antiforgery = new StubAntiforgery { IsValid = true };
        var filter = CreateFilter(antiforgery);
        var context = CreateContext(HttpMethods.Put, CookieAuthenticationDefaults.AuthenticationScheme);

        await filter.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
        Assert.Equal(1, antiforgery.ValidationCalls);
    }

    [Fact]
    public async Task UnsafeBearerRequest_DoesNotRequireAntiforgeryToken()
    {
        var antiforgery = new StubAntiforgery { IsValid = false };
        var filter = CreateFilter(antiforgery);
        var context = CreateContext(HttpMethods.Post, "Bearer");

        await filter.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
        Assert.Equal(0, antiforgery.ValidationCalls);
    }

    [Fact]
    public async Task SafeCookieRequest_DoesNotRequireAntiforgeryToken()
    {
        var antiforgery = new StubAntiforgery { IsValid = false };
        var filter = CreateFilter(antiforgery);
        var context = CreateContext(HttpMethods.Get, CookieAuthenticationDefaults.AuthenticationScheme);

        await filter.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
        Assert.Equal(0, antiforgery.ValidationCalls);
    }

    [Fact]
    public void SecurityMvcOptions_RegistersGlobalCookieFilterAndHeader()
    {
        var mvcOptions = new MvcOptions();
        var antiforgeryOptions = new AntiforgeryOptions();

        SecurityMvcOptions.ConfigureMvc(mvcOptions);
        SecurityMvcOptions.ConfigureAntiforgery(antiforgeryOptions);

        var typeFilter = Assert.IsType<TypeFilterAttribute>(Assert.Single(mvcOptions.Filters));
        Assert.Equal(typeof(CookieAntiforgeryFilter), typeFilter.ImplementationType);
        Assert.Equal(AntiforgeryConstants.HeaderName, antiforgeryOptions.HeaderName);
    }

    private static CookieAntiforgeryFilter CreateFilter(IAntiforgery antiforgery) =>
        new(antiforgery, NullLogger<CookieAntiforgeryFilter>.Instance);

    private static AuthorizationFilterContext CreateContext(string method, string authenticationType)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "7")],
            authenticationType));
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, []);
    }

    private sealed class StubAntiforgery : IAntiforgery
    {
        public bool IsValid { get; init; }

        public int ValidationCalls { get; private set; }

        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) =>
            new("request", "cookie", "field", "header");

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) =>
            new("request", "cookie", "field", "header");

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) =>
            Task.FromResult(IsValid);

        public Task ValidateRequestAsync(HttpContext httpContext)
        {
            ValidationCalls++;
            return IsValid
                ? Task.CompletedTask
                : Task.FromException(new AntiforgeryValidationException("Invalid token."));
        }

        public void SetCookieTokenAndHeader(HttpContext httpContext)
        {
        }
    }
}
