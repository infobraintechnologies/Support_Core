using System.ComponentModel.DataAnnotations;

namespace CBSSupport.Shared.Contracts;

public static class AttachmentStates
{
    public const string PendingUpload = "PendingUpload";
    public const string Uploaded = "Uploaded";
    public const string StructuralValidation = "StructuralValidation";
    public const string StructurallyValidated = "StructurallyValidated";
    public const string Scanning = "Scanning";
    public const string Promoting = "Promoting";
    public const string Ready = "Ready";
    public const string Rejected = "Rejected";
    public const string ScanFailed = "ScanFailed";
    public const string DeletePending = "DeletePending";
    public const string Deleted = "Deleted";
    public const string Expired = "Expired";

    public static bool IsTerminal(string state) =>
        state is Rejected or ScanFailed or Deleted or Expired;
}

public static class AttachmentRejectionCodes
{
    public const string ObjectChangedAfterComplete = "object_changed_after_complete";
    public const string ReadyObjectConflict = "ready_object_conflict";
    public const string MalwareDetected = "malware_detected";
    public const string ContentTypeMismatch = "content_type_mismatch";
    public const string MalformedContent = "malformed_content";
    public const string ActiveContent = "active_content";
    public const string EncryptedContent = "encrypted_content";
    public const string PackageLimitExceeded = "package_limit_exceeded";
    public const string ImageLimitExceeded = "image_limit_exceeded";
    public const string PdfLimitExceeded = "pdf_limit_exceeded";
    public const string InvalidContent = "invalid_content";
    public const string SizeMismatch = "size_mismatch";
    public const string UploadAbandoned = "upload_abandoned";
    public const string ScanAttemptsExhausted = "scan_attempts_exhausted";
    public const string ValidationAttemptsExhausted = "validation_attempts_exhausted";
    public const string UserCancelled = "user_cancelled";
}

public sealed record CreateAttachmentUploadRequest(
    [Required, StringLength(255, MinimumLength = 1)] string DisplayName,
    [Required, StringLength(128, MinimumLength = 1)] string MediaType,
    [Range(1, 10 * 1024 * 1024)] long Size);

public sealed record AttachmentUploadIntent(
    Guid Id,
    string UploadUrl,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    AttachmentSummary Attachment);

public sealed record AttachmentSummary(
    Guid Id,
    string DisplayName,
    string MediaType,
    long Size,
    string Status,
    string? RejectionCode,
    int? Position = null);

public sealed record AttachmentStatusResponse(
    Guid Id,
    long ConversationId,
    string DisplayName,
    string? MediaType,
    long Size,
    string Status,
    string? RejectionCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt,
    DateTimeOffset? ExpiresAt);

public sealed record AttachmentActor(
    long UserId,
    long? ClientId,
    bool IsAdmin);

public enum AttachmentCommandStatus
{
    Accepted,
    Success,
    Unavailable,
    Invalid,
    Unsupported,
    Conflict,
    QuotaExceeded,
    ScannerUnavailable
}

public sealed record AttachmentCommandResult<T>(
    AttachmentCommandStatus Status,
    T? Value = default,
    string? ErrorCode = null,
    int? RetryAfterSeconds = null);
