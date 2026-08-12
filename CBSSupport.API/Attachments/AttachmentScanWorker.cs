using System.Security.Cryptography;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Attachments;

public sealed class AttachmentScanWorker(
    IAttachmentRepository repository,
    IFileStorage storage,
    IFileScanner scanner,
    AttachmentOptions options,
    TimeProvider timeProvider,
    ILogger<AttachmentScanWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var leadership = await repository.TryAcquireWorkerLeadershipAsync(
                "attachment-scan-leader",
                stoppingToken);
            if (leadership is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await leadership.IsHeldAsync(stoppingToken))
                {
                    logger.LogWarning("Attachment scan leadership was lost; reacquiring");
                    break;
                }

                if (!scanner.Health.Healthy)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(options.Scanning.HealthCheckSeconds),
                        stoppingToken);
                    continue;
                }

                var now = timeProvider.GetUtcNow();
                var batch = await repository.ClaimScanBatchAsync(
                    _leaseOwner,
                    Math.Clamp(options.Scanning.MaxConcurrentScans, 1, 4),
                    now,
                    now.AddMinutes(3),
                    stoppingToken);
                if (batch.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                await Parallel.ForEachAsync(
                    batch,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Clamp(
                            options.Scanning.MaxConcurrentScans,
                            1,
                            4),
                        CancellationToken = stoppingToken
                    },
                    ProcessAsync);
            }
        }
    }

    private async ValueTask ProcessAsync(
        AttachmentRecord attachment,
        CancellationToken cancellationToken)
    {
        try
        {
            if (attachment.State == AttachmentStates.Promoting)
            {
                await PromoteAsync(attachment, cancellationToken);
                return;
            }
            if (attachment.QuarantineKey is null || attachment.SourceETag is null)
            {
                await RejectAsync(
                    attachment,
                    AttachmentRejectionCodes.InvalidContent,
                    AttachmentStates.ScanFailed,
                    cancellationToken);
                return;
            }

            AttachmentContentValidation validation;
            await using (var stored = await storage.OpenReadAsync(
                attachment.QuarantineKey,
                cancellationToken))
            {
                if (stored is null
                    || !ETagEquals(stored.Info.ETag, attachment.SourceETag))
                {
                    await RejectAsync(
                        attachment,
                        AttachmentRejectionCodes.ObjectChangedAfterComplete,
                        AttachmentStates.Rejected,
                        cancellationToken);
                    return;
                }
                if (stored.Info.Size != attachment.DeclaredSize
                    || stored.Info.Size != attachment.ActualSize
                    || stored.Info.Size is < 1
                    || stored.Info.Size > options.MaximumFileBytes)
                {
                    await RejectAsync(
                        attachment,
                        AttachmentRejectionCodes.SizeMismatch,
                        AttachmentStates.Rejected,
                        cancellationToken);
                    return;
                }

                using var memory = new MemoryStream();
                await CopyBoundedAsync(
                    stored.Content,
                    memory,
                    options.MaximumFileBytes,
                    cancellationToken);
                if (memory.Length != attachment.DeclaredSize
                    || memory.Length != attachment.ActualSize)
                {
                    await RejectAsync(
                        attachment,
                        AttachmentRejectionCodes.SizeMismatch,
                        AttachmentStates.Rejected,
                        cancellationToken);
                    return;
                }

                memory.Position = 0;
                validation = await AttachmentContentValidator.ValidateAsync(
                    memory,
                    attachment.DisplayName,
                    attachment.DeclaredMediaType,
                    options.MaximumFileBytes,
                    cancellationToken);
                if (!validation.Valid || validation.DetectedMediaType is null)
                {
                    await RejectAsync(
                        attachment,
                        validation.RejectionCode
                            ?? AttachmentRejectionCodes.InvalidContent,
                        AttachmentStates.Rejected,
                        cancellationToken);
                    return;
                }

                memory.Position = 0;
                var scan = await scanner.ScanAsync(memory, cancellationToken);
                if (scan.Status == FileScanStatus.Infected)
                {
                    await RejectAsync(
                        attachment,
                        AttachmentRejectionCodes.MalwareDetected,
                        AttachmentStates.Rejected,
                        cancellationToken);
                    return;
                }
                if (scan.Status == FileScanStatus.Unavailable)
                {
                    var health = await scanner.CheckHealthAsync(cancellationToken);
                    await RetryOrFailAsync(
                        attachment,
                        scan.ErrorCode ?? "clamav_unavailable",
                        consumeAttempt: health.Healthy,
                        cancellationToken);
                    return;
                }
            }

            var readyKey = $"{attachment.Id:D}{AttachmentContentValidator.GetExtensionForMediaType(validation.DetectedMediaType)}";
            var promoting = await repository.MarkPromotingAsync(
                attachment.Id,
                _leaseOwner,
                validation.DetectedMediaType,
                validation.Size,
                attachment.SourceETag,
                validation.Sha256,
                readyKey,
                timeProvider.GetUtcNow(),
                cancellationToken);
            if (promoting is not null)
            {
                await PromoteAsync(promoting, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Attachment scan failed for {AttachmentId}",
                attachment.Id);
            await RetryOrFailAsync(
                attachment,
                "attachment_scan_error",
                consumeAttempt: true,
                cancellationToken);
        }
    }

    internal ValueTask ProcessOnceAsync(
        AttachmentRecord attachment,
        CancellationToken cancellationToken = default) =>
        ProcessAsync(attachment, cancellationToken);

    private async Task PromoteAsync(
        AttachmentRecord attachment,
        CancellationToken cancellationToken)
    {
        if (attachment.QuarantineKey is null
            || attachment.ReadyKey is null
            || attachment.SourceETag is null
            || attachment.Sha256 is null
            || attachment.DetectedMediaType is null
            || attachment.ActualSize is null)
        {
            await RejectAsync(
                attachment,
                AttachmentRejectionCodes.InvalidContent,
                AttachmentStates.ScanFailed,
                cancellationToken);
            return;
        }

        var metadata = new ReadyObjectMetadata(
            attachment.Id,
            attachment.SourceETag,
            Convert.ToHexString(attachment.Sha256).ToLowerInvariant(),
            attachment.DetectedMediaType,
            attachment.ActualSize.Value);
        var result = await storage.PromoteAsync(
            attachment.QuarantineKey,
            attachment.ReadyKey,
            attachment.SourceETag,
            metadata,
            cancellationToken);
        switch (result)
        {
            case PromotionResult.Copied:
            case PromotionResult.ExistingExactMatch:
                var ready = await storage.HeadAsync(attachment.ReadyKey, cancellationToken);
                if (ready is null)
                {
                    await RetryOrFailAsync(
                        attachment,
                        "ready_object_missing",
                        consumeAttempt: true,
                        cancellationToken);
                    return;
                }
                var now = timeProvider.GetUtcNow();
                var finalized = await repository.MarkReadyAsync(
                    attachment.Id,
                    _leaseOwner,
                    ready.ETag,
                    now,
                    now.AddHours(options.ReadyUnboundHours),
                    cancellationToken);
                if (!finalized)
                {
                    return;
                }
                try
                {
                    await storage.DeleteIfExistsAsync(
                        attachment.QuarantineKey,
                        cancellationToken);
                    await repository.CompleteReadyQuarantineCleanupAsync(
                        attachment.Id,
                        _leaseOwner,
                        timeProvider.GetUtcNow(),
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Ready attachment {AttachmentId} retained a quarantine copy for cleanup",
                        attachment.Id);
                }
                return;
            case PromotionResult.RetryableConflict:
                await RetryOrFailAsync(
                    attachment,
                    "promotion_destination_race",
                    consumeAttempt: true,
                    cancellationToken);
                return;
            case PromotionResult.SourceChanged:
                await RejectAsync(
                    attachment,
                    AttachmentRejectionCodes.ObjectChangedAfterComplete,
                    AttachmentStates.Rejected,
                    cancellationToken);
                return;
            case PromotionResult.ReadyConflict:
                logger.LogCritical(
                    "Ready object metadata conflict for attachment {AttachmentId}",
                    attachment.Id);
                await RejectAsync(
                    attachment,
                    AttachmentRejectionCodes.ReadyObjectConflict,
                    AttachmentStates.ScanFailed,
                    cancellationToken);
                return;
            default:
                await RetryOrFailAsync(
                    attachment,
                    "promotion_source_missing",
                    consumeAttempt: true,
                    cancellationToken);
                return;
        }
    }

    private Task RejectAsync(
        AttachmentRecord attachment,
        string rejectionCode,
        string targetState,
        CancellationToken cancellationToken) =>
        repository.MarkRejectedForDeleteAsync(
            attachment.Id,
            _leaseOwner,
            rejectionCode,
            targetState,
            timeProvider.GetUtcNow(),
            cancellationToken);

    private async Task RetryOrFailAsync(
        AttachmentRecord attachment,
        string errorCode,
        bool consumeAttempt,
        CancellationToken cancellationToken)
    {
        var nextAttempt = attachment.AttemptCount + (consumeAttempt ? 1 : 0);
        if (consumeAttempt && nextAttempt >= options.Scanning.MaximumAttempts)
        {
            await RejectAsync(
                attachment,
                AttachmentRejectionCodes.ScanAttemptsExhausted,
                AttachmentStates.ScanFailed,
                cancellationToken);
            return;
        }

        var exponent = Math.Pow(2, Math.Clamp(attachment.AttemptCount, 0, 4));
        var baseSeconds = Math.Min(
            options.Scanning.MaximumBackoffSeconds,
            options.Scanning.MinimumBackoffSeconds * exponent);
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        await repository.ReleaseScanForRetryAsync(
            attachment.Id,
            _leaseOwner,
            timeProvider.GetUtcNow().AddSeconds(baseSeconds * jitter),
            errorCode,
            consumeAttempt,
            cancellationToken);
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("Attachment exceeded the configured scan limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool ETagEquals(string left, string right) =>
        string.Equals(
            left.Trim().Trim('"'),
            right.Trim().Trim('"'),
            StringComparison.Ordinal);
}
