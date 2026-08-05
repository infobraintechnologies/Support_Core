using CBSSupport.Shared.Contracts;

namespace CBSSupport.Shared.Services;

public interface IAttachmentService
{
    Task<AttachmentCommandResult<AttachmentUploadIntent>> CreateUploadIntentAsync(
        long conversationId,
        AttachmentActor actor,
        CreateAttachmentUploadRequest request,
        CancellationToken cancellationToken = default);

    Task<AttachmentCommandResult<AttachmentStatusResponse>> CompleteAsync(
        Guid attachmentId,
        AttachmentActor actor,
        CancellationToken cancellationToken = default);

    Task<AttachmentStatusResponse?> GetStatusAsync(
        Guid attachmentId,
        AttachmentActor actor,
        CancellationToken cancellationToken = default);

    Task<AttachmentCommandResult<bool>> CancelAsync(
        Guid attachmentId,
        AttachmentActor actor,
        CancellationToken cancellationToken = default);

    Task<AttachmentCommandResult<string>> CreateContentUrlAsync(
        Guid attachmentId,
        AttachmentActor actor,
        string disposition,
        CancellationToken cancellationToken = default);
}
