namespace CBSSupport.Shared.Services;

public sealed class DisabledFileStorage : IFileStorage
{
    private static InvalidOperationException Disabled() =>
        new("Attachment storage is disabled.");

    public Task<StoredObjectInfo> WriteAsync(
        string key,
        Stream content,
        string mediaType,
        long size,
        CancellationToken cancellationToken = default) =>
        Task.FromException<StoredObjectInfo>(Disabled());

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
