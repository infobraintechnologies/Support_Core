using Microsoft.Extensions.Options;

namespace CBSSupport.API.Configuration;

/// <summary>
/// Fails closed when a deployment attempts to enable Private messaging before the
/// content-free legacy mapping gate is clear. When the feature remains disabled,
/// operators use the deployment preflight before a future activation.
/// </summary>
public sealed class PrivateMessagingReadinessHostedService(
    IPrivateMessagingReadinessGate readinessGate,
    IOptions<MessagingFeatureOptions> featureOptions,
    ILogger<PrivateMessagingReadinessHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!featureOptions.Value.PrivateEnabled)
        {
            logger.LogInformation(
                "Private messaging is disabled; deployment preflight remains required before activation");
            return;
        }

        var readiness = await readinessGate.CheckAsync(cancellationToken);
        if (!readiness.IsReady)
        {
            logger.LogWarning(
                "Private messaging readiness is {Status}; unresolved reviews {NeedsReviewCount}; invalid rows {InvalidCount}",
                readiness.Status,
                readiness.NeedsReviewCount,
                readiness.InvalidCount);
            throw new InvalidOperationException(
                "Messaging:Features:PrivateEnabled cannot be true until the legacy Private mapping gate is Ready.");
        }

        logger.LogInformation("Private messaging readiness gate is Ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
