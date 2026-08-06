using CBSSupport.API.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CBSSupport.API.Tests.Configuration;

public sealed class PrivateMessagingReadinessHostedServiceTests
{
    [Fact]
    public async Task StartAsync_PrivateEnabledAndGateNotReady_FailsClosed()
    {
        var service = CreateService(privateEnabled: true, new(
            false, 2, 0, "NotReady"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_PrivateDisabledAndGateNotReady_ReportsWithoutBlockingStartup()
    {
        var service = CreateService(privateEnabled: false, new(
            false, 2, 0, "NotReady"));

        await service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_PrivateEnabledAndGateReady_AllowsStartup()
    {
        var service = CreateService(privateEnabled: true, new(
            true, 0, 0, "Ready"));

        await service.StartAsync(CancellationToken.None);
    }

    private static PrivateMessagingReadinessHostedService CreateService(
        bool privateEnabled,
        PrivateMessagingReadiness readiness) =>
        new(
            new FixedReadinessGate(readiness),
            Options.Create(new MessagingFeatureOptions { PrivateEnabled = privateEnabled }),
            NullLogger<PrivateMessagingReadinessHostedService>.Instance);

    private sealed class FixedReadinessGate(PrivateMessagingReadiness readiness)
        : IPrivateMessagingReadinessGate
    {
        public Task<PrivateMessagingReadiness> CheckAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(readiness);
    }
}
