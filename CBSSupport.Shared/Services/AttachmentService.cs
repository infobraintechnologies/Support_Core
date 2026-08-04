using CBSSupport.Shared.Contracts;

namespace CBSSupport.Shared.Services;

public sealed class AttachmentService(
    IAttachmentRepository repository,
    IConversationService conversations,
    IFileStorage storage,
    IFileScanner? scanner,
    AttachmentOptions options,
    TimeProvider timeProvider) : IAttachmentService
{
    public async Task<AttachmentCommandResult<AttachmentUploadIntent>> CreateUploadIntentAsync(
        long conversationId,
        AttachmentActor actor,
        CreateAttachmentUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return new(AttachmentCommandStatus.Unavailable, ErrorCode: "attachments_disabled");
        }
        if (conversationId <= 0
            || actor.UserId <= 0
            || request.Size is < 1
            || request.Size > options.MaximumFileBytes)
        {
            return new(AttachmentCommandStatus.Invalid, ErrorCode: "attachment_invalid");
        }

        if (!AttachmentContentValidator.IsAllowedDeclaration(
            request.DisplayName,
            request.MediaType,
            out var safeDisplayName))
        {
            return new(
                AttachmentCommandStatus.Unsupported,
                ErrorCode: "attachment_type_unsupported");
        }

        if (options.SecurityMode == AttachmentSecurityMode.MalwareScanning)
        {
            var health = scanner?.Health;
            if (health is null
                || !health.Healthy
                || timeProvider.GetUtcNow() - health.CheckedAt
                    > TimeSpan.FromSeconds(options.Scanning.HealthCheckSeconds * 2))
            {
                health = scanner is null
                    ? null
                    : await scanner.CheckHealthAsync(cancellationToken);
            }
            if (health is null || !health.Healthy)
            {
                return new(
                    AttachmentCommandStatus.ScannerUnavailable,
                    ErrorCode: health?.ErrorCode ?? "malware_scanner_unavailable",
                    RetryAfterSeconds: options.Scanning.HealthCheckSeconds);
            }
        }

        var conversationActor = new ConversationActor(
            actor.UserId,
            actor.ClientId,
            actor.IsAdmin,
            actor.IsAdmin ? "Administrator" : "Client");
        var access = await conversations.GetAccessAsync(
            conversationId,
            conversationActor,
            cancellationToken);
        if (access?.ClientId is not > 0)
        {
            return new(
                AttachmentCommandStatus.Unavailable,
                ErrorCode: "conversation_unavailable");
        }
        if (!access.IsGroup && !access.IsPrivate && !access.IsTicket && !access.IsInquiry)
        {
            return new(
                AttachmentCommandStatus.Unavailable,
                ErrorCode: "attachments_not_supported_for_conversation");
        }

        var now = timeProvider.GetUtcNow();
        var attachmentId = Guid.NewGuid();
        var quarantineKey =
            $"quarantine/{access.ClientId.Value}/{attachmentId:D}";
        var result = await repository.CreateIntentAsync(
            new AttachmentIntentRecord(
                attachmentId,
                access.ClientId.Value,
                conversationId,
                actor,
                quarantineKey,
                safeDisplayName,
                request.MediaType.Split(';', 2)[0].Trim().ToLowerInvariant(),
                request.Size,
                now),
            options,
            cancellationToken);
        if (result.Value is null)
        {
            return new(
                result.Status,
                ErrorCode: result.ErrorCode,
                RetryAfterSeconds: result.RetryAfterSeconds);
        }

        var lifetime = TimeSpan.FromSeconds(options.UploadUrlLifetimeSeconds);
        var uploadUrl = await storage.CreatePresignedPutUrlAsync(
            quarantineKey,
            result.Value.DeclaredMediaType,
            result.Value.DeclaredSize,
            lifetime,
            cancellationToken);
        var summary = ToSummary(result.Value);
        return new(
            AttachmentCommandStatus.Accepted,
            new AttachmentUploadIntent(
                attachmentId,
                uploadUrl,
                now.Add(lifetime),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Type"] = result.Value.DeclaredMediaType
                },
                summary));
    }

    public async Task<AttachmentCommandResult<AttachmentStatusResponse>> CompleteAsync(
        Guid attachmentId,
        AttachmentActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return new(AttachmentCommandStatus.Unavailable, ErrorCode: "attachments_disabled");
        }
        var attachment = await repository.GetAuthorizedAsync(
            attachmentId,
            actor,
            cancellationToken);
        if (attachment?.QuarantineKey is null)
        {
            return new(AttachmentCommandStatus.Unavailable, ErrorCode: "attachment_not_found");
        }
        if (attachment.State is not AttachmentStates.PendingUpload)
        {
            return new(
                attachment.State is AttachmentStates.Uploaded
                    or AttachmentStates.StructuralValidation
                    or AttachmentStates.StructurallyValidated
                    or AttachmentStates.Scanning
                    or AttachmentStates.Promoting
                    or AttachmentStates.Ready
                    ? AttachmentCommandStatus.Success
                    : AttachmentCommandStatus.Conflict,
                ToStatus(attachment),
                attachment.State is AttachmentStates.Uploaded
                    or AttachmentStates.StructuralValidation
                    or AttachmentStates.StructurallyValidated
                    or AttachmentStates.Scanning
                    or AttachmentStates.Promoting
                    or AttachmentStates.Ready
                    ? null
                    : "attachment_state_conflict");
        }

        var stored = await storage.HeadAsync(attachment.QuarantineKey, cancellationToken);
        if (stored is null)
        {
            return new(
                AttachmentCommandStatus.Conflict,
                ErrorCode: "upload_object_missing");
        }
        var result = await repository.CompleteAsync(
            attachmentId,
            actor,
            stored.Size,
            stored.ETag,
            timeProvider.GetUtcNow(),
            cancellationToken);
        return new(
            result.Status,
            result.Value is null ? null : ToStatus(result.Value),
            result.ErrorCode,
            result.RetryAfterSeconds);
    }

    public async Task<AttachmentStatusResponse?> GetStatusAsync(
        Guid attachmentId,
        AttachmentActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return null;
        }
        var attachment = await repository.GetAuthorizedAsync(
            attachmentId,
            actor,
            cancellationToken);
        return attachment is null ? null : ToStatus(attachment);
    }

    public async Task<AttachmentCommandResult<bool>> CancelAsync(
        Guid attachmentId,
        AttachmentActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return new(AttachmentCommandStatus.Unavailable, ErrorCode: "attachments_disabled");
        }
        var result = await repository.CancelAsync(
            attachmentId,
            actor,
            AttachmentRejectionCodes.UserCancelled,
            timeProvider.GetUtcNow(),
            cancellationToken);
        return new(
            result.Status,
            result.Value is not null,
            result.ErrorCode,
            result.RetryAfterSeconds);
    }

    public async Task<AttachmentCommandResult<string>> CreateContentUrlAsync(
        Guid attachmentId,
        AttachmentActor actor,
        string disposition,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return new(AttachmentCommandStatus.Unavailable, ErrorCode: "attachments_disabled");
        }
        if (disposition is not ("inline" or "attachment"))
        {
            return new(AttachmentCommandStatus.Invalid, ErrorCode: "invalid_disposition");
        }
        var attachment = await repository.GetReadyForContentAsync(
            attachmentId,
            actor,
            cancellationToken);
        if (attachment?.ReadyKey is null
            || attachment.State != AttachmentStates.Ready
            || attachment.DetectedMediaType is null
            || attachment.ActualSize is null)
        {
            return new(AttachmentCommandStatus.Unavailable, ErrorCode: "attachment_not_found");
        }
        var effectiveDisposition = AttachmentContentValidator.RequiresAttachmentDisposition(
            attachment.DetectedMediaType)
            ? "attachment"
            : disposition;
        var url = await storage.CreatePresignedGetUrlAsync(
            attachment.ReadyKey,
            effectiveDisposition,
            attachment.DisplayName,
            attachment.DetectedMediaType,
            TimeSpan.FromSeconds(options.DownloadUrlLifetimeSeconds),
            cancellationToken);
        return new(AttachmentCommandStatus.Success, url);
    }

    private static AttachmentSummary ToSummary(AttachmentRecord value) =>
        new(
            value.Id,
            value.DisplayName,
            value.DetectedMediaType ?? value.DeclaredMediaType,
            value.ActualSize ?? value.DeclaredSize,
            value.State,
            value.RejectionCode,
            value.Position);

    private static AttachmentStatusResponse ToStatus(AttachmentRecord value) =>
        new(
            value.Id,
            value.ConversationId,
            value.DisplayName,
            value.DetectedMediaType ?? value.DeclaredMediaType,
            value.ActualSize ?? value.DeclaredSize,
            value.State,
            value.RejectionCode,
            value.CreatedAt,
            value.ReadyAt,
            value.ExpiresAt);
}
