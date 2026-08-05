using System.Text.RegularExpressions;

namespace CBSSupport.API.Tests.Frontend;

public sealed partial class BrowserSignalRAuthenticationTests
{
    [Fact]
    public void FirstPartyBrowserScripts_SignalRAuthentication_DoesNotUseBearerTokens()
    {
        var scriptsDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "CBSSupport.API",
            "wwwroot",
            "js"));

        var firstPartyScripts = Directory
            .EnumerateFiles(scriptsDirectory, "*.js", SearchOption.AllDirectories)
            .Where(path => !IsVendoredSignalRScript(scriptsDirectory, path))
            .ToArray();

        Assert.NotEmpty(firstPartyScripts);

        foreach (var scriptPath in firstPartyScripts)
        {
            var source = File.ReadAllText(scriptPath);

            Assert.DoesNotContain("accessTokenFactory", source, StringComparison.Ordinal);
            Assert.DoesNotMatch(LocalStorageAccessTokenPattern(), source);
        }
    }

    [Fact]
    public void BrowserChatTransport_UsesSameOriginHubUrl()
    {
        var scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "CBSSupport.API",
            "wwwroot",
            "js",
            "messaging",
            "transport.js"));

        var source = File.ReadAllText(scriptPath);

        Assert.Contains("options.hubUrl || \"/chathub\"", source, StringComparison.Ordinal);
    }

    private static bool IsVendoredSignalRScript(string scriptsDirectory, string scriptPath)
    {
        var relativePath = Path.GetRelativePath(scriptsDirectory, scriptPath);
        var firstSegment = relativePath.Split(Path.DirectorySeparatorChar)[0];

        return firstSegment.Equals("signalr", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        "localStorage\\s*\\.\\s*(?:getItem|setItem)\\s*\\(\\s*['\\\"]accessToken['\\\"]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalStorageAccessTokenPattern();
}
