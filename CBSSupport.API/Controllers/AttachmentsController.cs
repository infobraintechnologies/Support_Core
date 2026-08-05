using CBSSupport.API.Security;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Controllers;

[ApiController]
[Authorize(Policy = Policies.AdminOrClient)]
public sealed class AttachmentsController(IAttachmentService attachments) : ControllerBase
{
    [HttpPost("api/v1/conversations/{conversationId:long}/attachment-uploads")]
    public async Task<ActionResult<AttachmentUploadIntent>> CreateUpload(
        long conversationId,
        CreateAttachmentUploadRequest request,
        CancellationToken cancellationToken)
    {
        var result = await attachments.CreateUploadIntentAsync(
            conversationId,
            GetRequiredActor(),
            request,
            cancellationToken);
        return result.Status switch
        {
            AttachmentCommandStatus.Accepted => Accepted(result.Value),
            AttachmentCommandStatus.Unsupported => ProblemResult(
                415,
                result.ErrorCode ?? "attachment_type_unsupported",
                "Unsupported attachment type"),
            AttachmentCommandStatus.QuotaExceeded => ProblemResult(
                429,
                result.ErrorCode ?? "attachment_quota_exceeded",
                "Attachment quota exceeded"),
            AttachmentCommandStatus.ScannerUnavailable => ScannerUnavailable(result),
            AttachmentCommandStatus.Unavailable => NotFound(),
            _ => ValidationProblem("The attachment upload request is invalid.")
        };
    }

    [HttpPost("api/v1/attachments/{attachmentId:guid}/complete")]
    public async Task<ActionResult<AttachmentStatusResponse>> Complete(
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        var result = await attachments.CompleteAsync(
            attachmentId,
            GetRequiredActor(),
            cancellationToken);
        return result.Status switch
        {
            AttachmentCommandStatus.Accepted => Accepted(result.Value),
            AttachmentCommandStatus.Success => Ok(result.Value),
            AttachmentCommandStatus.Unavailable => NotFound(),
            AttachmentCommandStatus.Conflict => ProblemResult(
                409,
                result.ErrorCode ?? "attachment_state_conflict",
                "Attachment state conflict"),
            _ => ValidationProblem("The attachment completion request is invalid.")
        };
    }

    [HttpGet("api/v1/attachments/{attachmentId:guid}")]
    public async Task<ActionResult<AttachmentStatusResponse>> GetStatus(
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        var result = await attachments.GetStatusAsync(
            attachmentId,
            GetRequiredActor(),
            cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("api/v1/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> Cancel(
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        var result = await attachments.CancelAsync(
            attachmentId,
            GetRequiredActor(),
            cancellationToken);
        return result.Status switch
        {
            AttachmentCommandStatus.Accepted or AttachmentCommandStatus.Success => NoContent(),
            AttachmentCommandStatus.Unavailable => NotFound(),
            AttachmentCommandStatus.Conflict => ProblemResult(
                409,
                result.ErrorCode ?? "attachment_cancel_conflict",
                "Attachment cannot be cancelled"),
            _ => ValidationProblem("The attachment cancellation request is invalid.")
        };
    }

    [HttpGet("api/v1/attachments/{attachmentId:guid}/content")]
    public async Task<IActionResult> Content(
        Guid attachmentId,
        [FromQuery] string disposition = "attachment",
        CancellationToken cancellationToken = default)
    {
        var result = await attachments.CreateContentUrlAsync(
            attachmentId,
            GetRequiredActor(),
            disposition,
            cancellationToken);
        Response.Headers.XContentTypeOptions = "nosniff";
        return result.Status switch
        {
            AttachmentCommandStatus.Success when result.Value is not null =>
                Redirect(result.Value),
            AttachmentCommandStatus.Unavailable => NotFound(),
            _ => ValidationProblem("Disposition must be inline or attachment.")
        };
    }

    private AttachmentActor GetRequiredActor()
    {
        var isAdmin = User.IsInRole(Roles.Admin);
        return new AttachmentActor(
            User.GetRequiredUserId(),
            isAdmin ? null : User.GetRequiredClientId(),
            isAdmin);
    }

    private ObjectResult ScannerUnavailable<T>(AttachmentCommandResult<T> result)
    {
        Response.Headers.RetryAfter = (result.RetryAfterSeconds ?? 60).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return ProblemResult(
            StatusCodes.Status503ServiceUnavailable,
            result.ErrorCode ?? "malware_scanner_unavailable",
            "Attachment malware scanning is temporarily unavailable");
    }

    private ObjectResult ProblemResult(int status, string code, string title) =>
        Problem(
            statusCode: status,
            title: title,
            detail: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
