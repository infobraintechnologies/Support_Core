using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Attachments;

public sealed class AttachmentValidationWorker(
    IAttachmentRepository repository,
    IFileStorage storage,
    AttachmentOptions options,
    TimeProvider timeProvider,
    ILogger<AttachmentValidationWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var leadership = await repository.TryAcquireWorkerLeadershipAsync(
                "attachment-structural-validation-leader",
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
                    logger.LogWarning("Attachment structural-validation leadership was lost; reacquiring");
                    break;
                }
                var now = timeProvider.GetUtcNow();
                var batch = await repository.ClaimProcessingBatchAsync(
                    _leaseOwner,
                    Math.Clamp(options.StructuralValidation.MaxConcurrentValidations, 1, 4),
                    now,
                    now.AddMinutes(3),
                    AttachmentSecurityMode.StructuralValidationOnly,
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
                            options.StructuralValidation.MaxConcurrentValidations,
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
            if (attachment.QuarantineKey is null || attachment.SourceETag is null)
            {
                await RejectAsync(attachment, AttachmentRejectionCodes.InvalidContent, cancellationToken);
                return;
            }
            await using var stored = await storage.OpenReadAsync(
                attachment.QuarantineKey,
                cancellationToken);
            if (stored is null || !ETagEquals(stored.Info.ETag, attachment.SourceETag))
            {
                await RejectAsync(
                    attachment,
                    AttachmentRejectionCodes.ObjectChangedAfterComplete,
                    cancellationToken);
                return;
            }
            if (stored.Info.Size != attachment.DeclaredSize
                || stored.Info.Size is < 1
                || stored.Info.Size > options.MaximumFileBytes)
            {
                await RejectAsync(attachment, AttachmentRejectionCodes.SizeMismatch, cancellationToken);
                return;
            }
            if (!string.Equals(
                NormalizeMediaType(stored.Info.ContentType),
                NormalizeMediaType(attachment.DeclaredMediaType),
                StringComparison.OrdinalIgnoreCase))
            {
                await RejectAsync(
                    attachment,
                    AttachmentRejectionCodes.ContentTypeMismatch,
                    cancellationToken);
                return;
            }

            var validation = await AttachmentContentValidator.ValidateAsync(
                stored.Content,
                attachment.DisplayName,
                attachment.DeclaredMediaType,
                options.MaximumFileBytes,
                cancellationToken,
                options.StructuralValidation);
            if (!validation.Valid
                || validation.DetectedMediaType is null
                || validation.CanonicalContent is null)
            {
                await RejectAsync(
                    attachment,
                    validation.RejectionCode ?? AttachmentRejectionCodes.InvalidContent,
                    cancellationToken);
                return;
            }

            var readyKey = attachment.ReadyKey
                ?? $"ready/{attachment.ClientId}/{attachment.Id:D}";
            if (attachment.State == AttachmentStates.StructuralValidation)
            {
                attachment = await repository.MarkStructurallyValidatedAsync(
                    attachment.Id,
                    _leaseOwner,
                    validation.DetectedMediaType,
                    validation.Size,
                    attachment.SourceETag!,
                    validation.Sha256,
                    readyKey,
                    timeProvider.GetUtcNow(),
                    cancellationToken) ?? attachment;
                if (attachment.State != AttachmentStates.StructurallyValidated)
                {
                    return;
                }
            }
            else if (!ValidationMetadataMatches(attachment, validation, readyKey))
            {
                await RejectAsync(attachment, AttachmentRejectionCodes.InvalidContent, cancellationToken);
                return;
            }

            if (attachment.State == AttachmentStates.StructurallyValidated)
            {
                attachment = await repository.MarkPromotingAsync(
                    attachment.Id,
                    _leaseOwner,
                    validation.DetectedMediaType,
                    validation.Size,
                    attachment.SourceETag!,
                    validation.Sha256,
                    readyKey,
                    timeProvider.GetUtcNow(),
                    cancellationToken) ?? attachment;
                if (attachment.State != AttachmentStates.Promoting)
                {
                    return;
                }
            }
            await StoreAndFinalizeAsync(attachment, validation.CanonicalContent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Attachment structural validation failed for {AttachmentId}",
                attachment.Id);
            await RetryOrFailAsync(attachment, "attachment_validation_error", cancellationToken);
        }
    }

    internal ValueTask ProcessOnceAsync(
        AttachmentRecord attachment,
        CancellationToken cancellationToken = default) =>
        ProcessAsync(attachment, cancellationToken);

    private async Task StoreAndFinalizeAsync(
        AttachmentRecord attachment,
        byte[] canonicalContent,
        CancellationToken cancellationToken)
    {
        if (attachment.QuarantineKey is null
            || attachment.ReadyKey is null
            || attachment.SourceETag is null
            || attachment.Sha256 is null
            || attachment.DetectedMediaType is null
            || attachment.ActualSize is null)
        {
            await RejectAsync(attachment, AttachmentRejectionCodes.InvalidContent, cancellationToken);
            return;
        }
        var metadata = new ReadyObjectMetadata(
            attachment.Id,
            attachment.SourceETag,
            Convert.ToHexString(attachment.Sha256).ToLowerInvariant(),
            attachment.DetectedMediaType,
            attachment.ActualSize.Value);
        using var stream = new MemoryStream(canonicalContent, writable: false);
        var result = await storage.StoreValidatedAsync(
            attachment.QuarantineKey,
            attachment.ReadyKey,
            attachment.SourceETag,
            metadata,
            stream,
            cancellationToken);
        switch (result)
        {
            case ValidatedWriteResult.Written:
            case ValidatedWriteResult.ExistingExactMatch:
                var ready = await storage.HeadAsync(attachment.ReadyKey, cancellationToken);
                if (ready is null)
                {
                    await RetryOrFailAsync(attachment, "ready_object_missing", cancellationToken);
                    return;
                }
                var now = timeProvider.GetUtcNow();
                if (!await repository.MarkReadyAsync(
                    attachment.Id,
                    _leaseOwner,
                    ready.ETag,
                    now,
                    now.AddHours(options.ReadyUnboundHours),
                    cancellationToken))
                {
                    return;
                }
                try
                {
                    await storage.DeleteIfExistsAsync(attachment.QuarantineKey, cancellationToken);
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
            case ValidatedWriteResult.SourceChanged:
                await RejectAsync(
                    attachment,
                    AttachmentRejectionCodes.ObjectChangedAfterComplete,
                    cancellationToken);
                return;
            case ValidatedWriteResult.ReadyConflict:
                logger.LogCritical(
                    "Ready object metadata conflict for attachment {AttachmentId}",
                    attachment.Id);
                await RejectAsync(
                    attachment,
                    AttachmentRejectionCodes.ReadyObjectConflict,
                    cancellationToken);
                return;
            default:
                await RetryOrFailAsync(attachment, "validated_write_conflict", cancellationToken);
                return;
        }
    }

    private Task RejectAsync(
        AttachmentRecord attachment,
        string rejectionCode,
        CancellationToken cancellationToken) =>
        repository.MarkRejectedForDeleteAsync(
            attachment.Id,
            _leaseOwner,
            rejectionCode,
            AttachmentStates.Rejected,
            timeProvider.GetUtcNow(),
            cancellationToken);

    private async Task RetryOrFailAsync(
        AttachmentRecord attachment,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var nextAttempt = attachment.AttemptCount + 1;
        if (nextAttempt >= options.StructuralValidation.MaximumAttempts)
        {
            await RejectAsync(
                attachment,
                AttachmentRejectionCodes.ValidationAttemptsExhausted,
                cancellationToken);
            return;
        }
        var exponent = Math.Pow(2, Math.Clamp(attachment.AttemptCount, 0, 4));
        var baseSeconds = Math.Min(
            options.StructuralValidation.MaximumBackoffSeconds,
            options.StructuralValidation.MinimumBackoffSeconds * exponent);
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        await repository.ReleaseScanForRetryAsync(
            attachment.Id,
            _leaseOwner,
            timeProvider.GetUtcNow().AddSeconds(baseSeconds * jitter),
            errorCode,
            consumeAttempt: true,
            cancellationToken: cancellationToken);
    }

    private static bool ValidationMetadataMatches(
        AttachmentRecord attachment,
        AttachmentContentValidation validation,
        string readyKey) =>
        attachment.ReadyKey == readyKey
        && attachment.ActualSize == validation.Size
        && attachment.DetectedMediaType == validation.DetectedMediaType
        && attachment.Sha256 is not null
        && attachment.Sha256.AsSpan().SequenceEqual(validation.Sha256);

    private static bool ETagEquals(string left, string right) =>
        string.Equals(
            left.Trim().Trim('"'),
            right.Trim().Trim('"'),
            StringComparison.Ordinal);

    private static string NormalizeMediaType(string? value) =>
        (value ?? string.Empty).Split(';', 2)[0].Trim().ToLowerInvariant();
}
