using CBSSupport.Shared.Services;

namespace CBSSupport.API.Attachments;

public sealed class ClamAvHealthMonitor(
    IFileScanner scanner,
    AttachmentOptions options,
    ILogger<ClamAvHealthMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var health = await scanner.CheckHealthAsync(stoppingToken);
            if (!health.Healthy)
            {
                logger.LogWarning(
                    "Attachment scanner health is degraded with code {ErrorCode}",
                    health.ErrorCode);
            }
            await Task.Delay(
                TimeSpan.FromSeconds(options.Scanning.HealthCheckSeconds),
                stoppingToken);
        }
    }
}
