using CBSSupport.Shared.Services;

namespace CBSSupport.API.Attachments;

public sealed class AttachmentCleanupWorker(
    IAttachmentRepository repository,
    IFileStorage storage,
    TimeProvider timeProvider,
    ILogger<AttachmentCleanupWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var leadership = await repository.TryAcquireWorkerLeadershipAsync(
                "attachment-cleanup-leader",
                stoppingToken);
            if (leadership is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await leadership.IsHeldAsync(stoppingToken))
                {
                    logger.LogWarning("Attachment cleanup leadership was lost; reacquiring");
                    break;
                }

                var now = timeProvider.GetUtcNow();
                var quarantineCleanup = await repository.ClaimReadyQuarantineCleanupBatchAsync(
                    _leaseOwner,
                    50,
                    now,
                    now.AddMinutes(3),
                    stoppingToken);
                foreach (var attachment in quarantineCleanup)
                {
                    await DeleteReadyQuarantineAsync(attachment, stoppingToken);
                }

                var batch = await repository.ClaimCleanupBatchAsync(
                    _leaseOwner,
                    50,
                    now,
                    now.AddMinutes(3),
                    stoppingToken);
                if (batch.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                foreach (var attachment in batch)
                {
                    await ProcessDeletionAsync(attachment, stoppingToken);
                }
            }
        }
    }

    internal Task ProcessDeletionOnceAsync(
        AttachmentRecord attachment,
        CancellationToken cancellationToken = default) =>
        ProcessDeletionAsync(attachment, cancellationToken);

    private async Task ProcessDeletionAsync(
        AttachmentRecord attachment,
        CancellationToken cancellationToken)
    {
        try
        {
            if (attachment.ReadyKey is not null)
            {
                await storage.DeleteIfExistsAsync(
                    attachment.ReadyKey,
                    cancellationToken);
            }
            if (attachment.QuarantineKey is not null)
            {
                await storage.DeleteIfExistsAsync(
                    attachment.QuarantineKey,
                    cancellationToken);
            }
            await repository.FinalizeDeletionAsync(
                attachment.Id,
                _leaseOwner,
                attachment.DeleteTargetState ?? "Deleted",
                attachment.RejectionCode,
                timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Attachment cleanup failed for {AttachmentId}",
                attachment.Id);
            await repository.ReleaseDeletionForRetryAsync(
                attachment.Id,
                _leaseOwner,
                timeProvider.GetUtcNow().AddMinutes(1),
                "attachment_delete_failed",
                cancellationToken);
        }
    }

    private async Task DeleteReadyQuarantineAsync(
        AttachmentRecord attachment,
        CancellationToken cancellationToken)
    {
        try
        {
            if (attachment.QuarantineKey is not null)
            {
                await storage.DeleteIfExistsAsync(
                    attachment.QuarantineKey,
                    cancellationToken);
            }
            await repository.CompleteReadyQuarantineCleanupAsync(
                attachment.Id,
                _leaseOwner,
                timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Ready attachment quarantine cleanup failed for {AttachmentId}",
                attachment.Id);
            await repository.ReleaseReadyQuarantineCleanupForRetryAsync(
                attachment.Id,
                _leaseOwner,
                timeProvider.GetUtcNow().AddMinutes(1),
                "quarantine_delete_failed",
                cancellationToken);
        }
    }
}
