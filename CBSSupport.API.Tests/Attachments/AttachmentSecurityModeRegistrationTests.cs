using CBSSupport.API.Attachments;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CBSSupport.API.Tests.Attachments;

public sealed class AttachmentSecurityModeRegistrationTests
{
    [Fact]
    public void StructuralValidationOnly_RegistersValidationWorkerWithoutClamAvServices()
    {
        using var factory = new StructuralValidationApplicationFactory();

        Assert.NotNull(factory.Services.GetRequiredService<AttachmentOptions>());
        Assert.Null(factory.Services.GetService<IFileScanner>());
        Assert.IsType<LocalAttachmentStorage>(factory.Services.GetRequiredService<IFileStorage>());
        Assert.True(factory.Services
            .GetRequiredService<AttachmentUiCapability>()
            .CanCreateUploadIntents);
        Assert.Contains(typeof(AttachmentValidationWorker), factory.RegisteredHostedServiceTypes);
        Assert.DoesNotContain(typeof(AttachmentScanWorker), factory.RegisteredHostedServiceTypes);
        Assert.DoesNotContain(typeof(ClamAvHealthMonitor), factory.RegisteredHostedServiceTypes);
    }

    private sealed class StructuralValidationApplicationFactory : WebApplicationFactory<Program>
    {
        public IReadOnlyCollection<Type> RegisteredHostedServiceTypes { get; private set; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting(
                "ConnectionStrings:DefaultConnection",
                "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("Jwt:Enabled", "false");
            builder.UseSetting("Security:PasswordHashing:Pepper", "test-company-pepper");
            builder.UseSetting("Attachments:Enabled", "true");
            builder.UseSetting("Attachments:SecurityMode", "StructuralValidationOnly");
            builder.UseSetting("Attachments:Scanning:WorkerEnabled", "true");
            builder.UseSetting("Attachments:Scanning:Host", "unresolvable-clamav.invalid");
            builder.UseSetting("Attachments:Scanning:Port", "3310");
            builder.ConfigureTestServices(services =>
            {
                RegisteredHostedServiceTypes = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                    .Select(descriptor => descriptor.ImplementationType)
                    .OfType<Type>()
                    .ToArray();

                // Registration is the subject of this test. Removing hosted services before
                // start prevents unrelated database/outbox work from running in the test host.
                services.RemoveAll<IHostedService>();
            });
        }
    }
}
