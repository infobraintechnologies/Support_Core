using System.Security.Claims;
using CBSSupport.API.Configuration;
using CbsDataProtectionOptions = CBSSupport.API.Configuration.DataProtectionOptions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Security;

public sealed class DataProtectionPersistenceTests
{
    [Fact]
    public void ProductionWithoutDurablePath_IsRejected()
    {
        var options = new CbsDataProtectionOptions();
        var environment = new TestHostEnvironment("Production");

        var exception = Assert.Throws<InvalidOperationException>(
            () =>
            {
                _ = options.ResolveKeyRingPath(environment, isOpenApiGeneration: false);
            });

        Assert.Contains("durable shared location", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRelativePath_IsRejected()
    {
        var options = new CbsDataProtectionOptions { KeyRingPath = "keys" };
        var environment = new TestHostEnvironment("Production");
        var resolvedPath = options.ResolveKeyRingPath(environment, isOpenApiGeneration: false);

        var exception = Assert.Throws<InvalidOperationException>(
            () => options.Validate(environment, resolvedPath, isOpenApiGeneration: false));

        Assert.Contains("absolute path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionEphemeralProvider_IsRejectedAtStartup()
    {
        var options = new CbsDataProtectionOptions
        {
            KeyRingPath = Path.Combine(Path.GetTempPath(), "not-used")
        };
        var validator = new DataProtectionStartupValidator(
            new TestHostEnvironment("Production"),
            options,
            new EphemeralDataProtectionProvider(),
            NullLogger<DataProtectionStartupValidator>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("ephemeral", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CookieArtifact_RemainsValidAfterProviderRecreatedWithSameKeyRing()
    {
        var keyRingPath = Path.Combine(
            Path.GetTempPath(),
            "CBSSupport-DataProtectionTests",
            Guid.NewGuid().ToString("N"));

        try
        {
            string protectedCookie;
            using (var firstHost = CreateDataProtectionHost(keyRingPath))
            {
                var provider = firstHost.GetRequiredService<IDataProtectionProvider>();
                var format = CreateCookieTicketDataFormat(provider);
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "restart-test-user")],
                    CookieAuthenticationDefaults.AuthenticationScheme);
                var ticket = new AuthenticationTicket(
                    new ClaimsPrincipal(identity),
                    CookieAuthenticationDefaults.AuthenticationScheme);

                protectedCookie = format.Protect(ticket);
            }

            // A new service provider models a process restart. Only the durable
            // key-ring directory is shared between the two hosts.
            using var restartedHost = CreateDataProtectionHost(keyRingPath);
            var restartedProvider = restartedHost.GetRequiredService<IDataProtectionProvider>();
            var restoredTicket = CreateCookieTicketDataFormat(restartedProvider).Unprotect(protectedCookie);

            Assert.NotNull(restoredTicket);
            Assert.Equal(
                "restart-test-user",
                restoredTicket!.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
        }
        finally
        {
            if (Directory.Exists(keyRingPath))
            {
                Directory.Delete(keyRingPath, recursive: true);
            }
        }
    }

    private static ServiceProvider CreateDataProtectionHost(string keyRingPath) =>
        new ServiceCollection()
            .AddDataProtection()
            .SetApplicationName("CBSSupport")
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
            .Services
            .BuildServiceProvider();

    private static TicketDataFormat CreateCookieTicketDataFormat(IDataProtectionProvider provider) =>
        new(provider.CreateProtector(
            "Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware",
            CookieAuthenticationDefaults.AuthenticationScheme,
            "v2"));

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "CBSSupport.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
