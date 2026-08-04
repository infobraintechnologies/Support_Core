using CBSSupport.Shared.Contracts;

namespace CBSSupport.Shared.Services;

public interface IAttachmentRepository
{
    Task<AttachmentCommandResult<AttachmentRecord>> CreateIntentAsync(
        AttachmentIntentRecord intent,
        AttachmentOptions options,
        CancellationToken cancellationToken = default);

    Task<AttachmentRecord?> GetAuthorizedAsync(
        Guid attachmentId,
        AttachmentActor actor,
        CancellationToken cancellationToken = default);

    Task<AttachmentRecord?> GetReadyForContentAsync(
        Guid attachmentId,
        AttachmentActor actor,
        CancellationToken cancellationToken = default);

    Task<AttachmentCommandResult<AttachmentRecord>> CompleteAsync(
        Guid attachmentId,
        AttachmentActor actor,
        long actualSize,
        string sourceETag,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    Task<AttachmentCommandResult<AttachmentRecord>> CancelAsync(
        Guid attachmentId,
        AttachmentActor actor,
        string rejectionCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IWorkerLeadershipLease?> TryAcquireWorkerLeadershipAsync(
        string workerName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttachmentRecord>> ClaimScanBatchAsync(
        string leaseOwner,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttachmentRecord>> ClaimProcessingBatchAsync(
        string leaseOwner,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        AttachmentSecurityMode securityMode,
        CancellationToken cancellationToken = default) =>
        ClaimScanBatchAsync(leaseOwner, batchSize, now, leaseUntil, cancellationToken);

    Task<AttachmentRecord?> MarkStructurallyValidatedAsync(
        Guid attachmentId,
        string leaseOwner,
        string detectedMediaType,
        long canonicalSize,
        string sourceETag,
        byte[] sha256,
        string readyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AttachmentRecord?>(
            new NotSupportedException("Structural validation is not implemented by this repository."));

    Task<AttachmentRecord?> MarkPromotingAsync(
        Guid attachmentId,
        string leaseOwner,
        string detectedMediaType,
        long actualSize,
        string sourceETag,
        byte[] sha256,
        string readyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> MarkReadyAsync(
        Guid attachmentId,
        string leaseOwner,
        string readyETag,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    Task MarkRejectedForDeleteAsync(
        Guid attachmentId,
        string leaseOwner,
        string rejectionCode,
        string targetState,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task ReleaseScanForRetryAsync(
        Guid attachmentId,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        string errorCode,
        bool consumeAttempt,
        CancellationToken cancellationToken = default);

    Task ReleaseDeletionForRetryAsync(
        Guid attachmentId,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        string errorCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttachmentRecord>> ClaimCleanupBatchAsync(
        string leaseOwner,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttachmentRecord>> ClaimReadyQuarantineCleanupBatchAsync(
        string leaseOwner,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteReadyQuarantineCleanupAsync(
        Guid attachmentId,
        string leaseOwner,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task ReleaseReadyQuarantineCleanupForRetryAsync(
        Guid attachmentId,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        string errorCode,
        CancellationToken cancellationToken = default);

    Task FinalizeDeletionAsync(
        Guid attachmentId,
        string leaseOwner,
        string targetState,
        string? rejectionCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
