namespace CBSSupport.Shared.Services;

public sealed class DisabledFileStorage : IFileStorage
{
    private static InvalidOperationException Disabled() =>
        new("Attachment storage is disabled.");

    public Task<string> CreatePresignedPutUrlAsync(
        string key,
        string mediaType,
        long size,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(Disabled());

    public Task<string> CreatePresignedGetUrlAsync(
        string key,
        string disposition,
        string displayName,
        string mediaType,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(Disabled());

    public Task<StoredObjectInfo?> HeadAsync(
        string key,
        CancellationToken cancellationToken = default) =>
        Task.FromException<StoredObjectInfo?>(Disabled());

    public Task<StoredObjectRead?> OpenReadAsync(
        string key,
        CancellationToken cancellationToken = default) =>
        Task.FromException<StoredObjectRead?>(Disabled());

    public Task<PromotionResult> PromoteAsync(
        string quarantineKey,
        string readyKey,
        string expectedSourceETag,
        ReadyObjectMetadata metadata,
        CancellationToken cancellationToken = default) =>
        Task.FromException<PromotionResult>(Disabled());

    public Task<ValidatedWriteResult> StoreValidatedAsync(
        string quarantineKey,
        string readyKey,
        string expectedSourceETag,
        ReadyObjectMetadata metadata,
        Stream validatedContent,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ValidatedWriteResult>(Disabled());

    public Task DeleteIfExistsAsync(
        string key,
        CancellationToken cancellationToken = default) =>
        Task.FromException(Disabled());
}
