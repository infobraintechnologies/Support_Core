using System.Reflection;
using CBSSupport.API.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Tests.Frontend;

public sealed class BrowserAntiforgeryTests
{
    [Theory]
    [InlineData("Views/Support/Index.cshtml")]
    [InlineData("Views/AdminSupport/Index.cshtml")]
    [InlineData("Views/Shared/_Layout.cshtml")]
    public void BrowserView_EmitsTokenAndLoadsSharedAntiforgeryFetchGuard(string relativePath)
    {
        var source = File.ReadAllText(GetApiPath(relativePath));

        Assert.Contains("@Html.AntiForgeryToken()", source, StringComparison.Ordinal);
        Assert.Contains("~/js/security/antiforgery.js", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFetchGuard_AddsTokenOnlyToSameOriginUnsafeRequests()
    {
        var source = File.ReadAllText(GetApiPath("wwwroot/js/security/antiforgery.js"));

        Assert.Contains("RequestVerificationToken", source, StringComparison.Ordinal);
        Assert.Contains("requestUrl.origin !== window.location.origin", source, StringComparison.Ordinal);
        Assert.Contains("unsafeMethods.has(method)", source, StringComparison.Ordinal);
        Assert.Contains("credentials:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void JwtTokenEndpoint_ExplicitlyIgnoresBrowserAntiforgeryValidation()
    {
        var action = typeof(AuthController).GetMethod(
            nameof(AuthController.GetToken),
            BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(action?.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
    }

    private static string GetApiPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "CBSSupport.API",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
