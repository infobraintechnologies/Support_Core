using CBSSupport.Shared.Contracts;

namespace CBSSupport.Shared.Services;

public sealed record StoredObjectInfo(
    string Key,
    long Size,
    string ETag,
    string? ContentType,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record StoredObjectRead(
    StoredObjectInfo Info,
    Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record ReadyObjectMetadata(
    Guid AttachmentId,
    string SourceETag,
    string Sha256,
    string MediaType,
    long Size);

public enum PromotionResult
{
    Copied,
    ExistingExactMatch,
    MissingSource,
    SourceChanged,
    ReadyConflict,
    RetryableConflict
}

public enum ValidatedWriteResult
{
    Written,
    ExistingExactMatch,
    SourceChanged,
    ReadyConflict,
    RetryableConflict
}

public interface IFileStorage
{
    Task<StoredObjectInfo> WriteAsync(
        string key,
        Stream content,
        string mediaType,
        long size,
        CancellationToken cancellationToken = default);

    Task<StoredObjectInfo?> HeadAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task<StoredObjectRead?> OpenReadAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task<PromotionResult> PromoteAsync(
        string quarantineKey,
        string readyKey,
        string expectedSourceETag,
        ReadyObjectMetadata metadata,
        CancellationToken cancellationToken = default);

    Task<ValidatedWriteResult> StoreValidatedAsync(
        string quarantineKey,
        string readyKey,
        string expectedSourceETag,
        ReadyObjectMetadata metadata,
        Stream validatedContent,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ValidatedWriteResult>(
            new NotSupportedException("Validated writes are not implemented by this storage adapter."));

    Task DeleteIfExistsAsync(
        string key,
        CancellationToken cancellationToken = default);
}

public sealed class AttachmentStorageConflictException(string message) : IOException(message);

public sealed record AttachmentContentRead(
    Stream Content,
    string DisplayName,
    string MediaType,
    string Disposition);

public enum FileScanStatus
{
    Clean,
    Infected,
    Unavailable
}

public sealed record FileScanResult(
    FileScanStatus Status,
    string? Signature = null,
    string? ErrorCode = null);

public sealed record FileScannerHealth(
    bool Healthy,
    DateTimeOffset CheckedAt,
    DateTimeOffset? DefinitionsUpdatedAt,
    string? ErrorCode);

public interface IFileScanner
{
    FileScannerHealth Health { get; }

    Task<FileScannerHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default);

    Task<FileScanResult> ScanAsync(
        Stream content,
        CancellationToken cancellationToken = default);
}

public sealed record AttachmentRecord(
    Guid Id,
    long ClientId,
    long ConversationId,
    long? MessageId,
    int? Position,
    int? AdminUserId,
    int? ClientUserId,
    string State,
    string? QuarantineKey,
    string? ReadyKey,
    string DisplayName,
    string DeclaredMediaType,
    string? DetectedMediaType,
    long DeclaredSize,
    long? ActualSize,
    long ReservationBytes,
    string? SourceETag,
    string? ExpectedReadyETag,
    byte[]? Sha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? UploadCompletedAt,
    DateTimeOffset? ReadyAt,
    DateTimeOffset? BoundAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? DeletedAt,
    string? LeaseOwner,
    DateTimeOffset? LeaseUntil,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    string? RejectionCode,
    string? DeleteTargetState,
    string? LastErrorCode,
    int DeletionAttemptCount);

public sealed record AttachmentIntentRecord(
    Guid Id,
    long ClientId,
    long ConversationId,
    AttachmentActor Actor,
    string QuarantineKey,
    string DisplayName,
    string DeclaredMediaType,
    long DeclaredSize,
    DateTimeOffset CreatedAt);

public interface IWorkerLeadershipLease : IAsyncDisposable
{
    Task<bool> IsHeldAsync(CancellationToken cancellationToken = default);
}
