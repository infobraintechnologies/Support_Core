using System.Security.Claims;
using System.Text.Json;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Tests.Controllers;

public sealed class AttachmentsControllerTests
{
    private static readonly Guid AttachmentId = Guid.Parse(
        "8d887a42-bd86-45d3-a32e-fc67a3ea1550");

    [Theory]
    [InlineData(AttachmentCommandStatus.Accepted, StatusCodes.Status202Accepted, null)]
    [InlineData(AttachmentCommandStatus.Unsupported, StatusCodes.Status415UnsupportedMediaType, "attachment_extension_unsupported")]
    [InlineData(AttachmentCommandStatus.QuotaExceeded, StatusCodes.Status429TooManyRequests, "attachment_tenant_storage_quota")]
    [InlineData(AttachmentCommandStatus.ScannerUnavailable, StatusCodes.Status503ServiceUnavailable, "clamav_definitions_stale")]
    [InlineData(AttachmentCommandStatus.Unavailable, StatusCodes.Status404NotFound, "conversation_unavailable")]
    [InlineData(AttachmentCommandStatus.Invalid, StatusCodes.Status400BadRequest, "attachment_invalid")]
    public async Task CreateUpload_MapsStatusAndStableCode(
        AttachmentCommandStatus commandStatus,
        int expectedStatus,
        string? errorCode)
    {
        var service = new RecordingAttachmentService
        {
            CreateResult = new(
                commandStatus,
                commandStatus == AttachmentCommandStatus.Accepted
                    ? CreateIntent()
                    : null,
                errorCode,
                commandStatus == AttachmentCommandStatus.ScannerUnavailable ? 37 : null)
        };
        var controller = CreateController(service);

        var result = await controller.CreateUpload(
            25,
            new CreateAttachmentUploadRequest("report.pdf", "application/pdf", 100),
            CancellationToken.None);

        if (expectedStatus == StatusCodes.Status400BadRequest)
        {
            Assert.IsType<ObjectResult>(result.Result);
            Assert.NotEqual(
                StatusCodes.Status415UnsupportedMediaType,
                GetStatusCode(result.Result));
        }
        else
        {
            Assert.Equal(expectedStatus, GetStatusCode(result.Result));
        }
        Assert.Equal(42, service.LastActor?.ClientId);
        Assert.Equal(7, service.LastActor?.UserId);
        Assert.False(service.LastActor?.IsAdmin);
        if (expectedStatus is 415 or 429 or 503)
        {
            var problem = Assert.IsType<ProblemDetails>(
                Assert.IsType<ObjectResult>(result.Result).Value);
            Assert.Equal(errorCode, problem.Extensions["code"]);
        }
        if (expectedStatus == StatusCodes.Status503ServiceUnavailable)
        {
            Assert.Equal("37", controller.Response.Headers.RetryAfter);
        }
    }

    [Fact]
    public async Task UnsupportedStatus_Is415OnlyOnSynchronousUploadDeclaration()
    {
        var service = new RecordingAttachmentService
        {
            CreateResult = new(
                AttachmentCommandStatus.Unsupported,
                ErrorCode: "attachment_type_unsupported"),
            CompleteResult = new(
                AttachmentCommandStatus.Unsupported,
                ErrorCode: "content_type_mismatch"),
            CancelResult = new(
                AttachmentCommandStatus.Unsupported,
                ErrorCode: "content_type_mismatch"),
            ContentResult = new(
                AttachmentCommandStatus.Unsupported,
                ErrorCode: "content_type_mismatch")
        };
        var controller = CreateController(service);

        var create = await controller.CreateUpload(
            25,
            new CreateAttachmentUploadRequest("payload.exe", "application/octet-stream", 100),
            CancellationToken.None);
        var complete = await controller.Complete(AttachmentId, CancellationToken.None);
        var cancel = await controller.Cancel(AttachmentId, CancellationToken.None);
        var content = await controller.Content(
            AttachmentId,
            cancellationToken: CancellationToken.None);

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, GetStatusCode(create.Result));
        Assert.IsType<ObjectResult>(complete.Result);
        Assert.IsType<ObjectResult>(cancel);
        Assert.IsType<ObjectResult>(content);
        Assert.NotEqual(
            StatusCodes.Status415UnsupportedMediaType,
            GetStatusCode(complete.Result));
        Assert.NotEqual(
            StatusCodes.Status415UnsupportedMediaType,
            GetStatusCode(cancel));
        Assert.NotEqual(
            StatusCodes.Status415UnsupportedMediaType,
            GetStatusCode(content));
    }

    [Fact]
    public async Task GetStatus_PostUploadRejection_Returns200WithStableAsynchronousCode()
    {
        var rejected = CreateStatus(
            AttachmentStates.Rejected,
            AttachmentRejectionCodes.ContentTypeMismatch);
        var service = new RecordingAttachmentService { StatusResult = rejected };
        var controller = CreateController(service);

        var result = await controller.GetStatus(AttachmentId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(rejected, ok.Value);
        Assert.Equal(AttachmentStates.Rejected, rejected.Status);
        Assert.Equal(
            AttachmentRejectionCodes.ContentTypeMismatch,
            rejected.RejectionCode);
    }

    [Theory]
    [InlineData("upload_object_missing")]
    [InlineData("attachment_state_conflict")]
    [InlineData("object_changed_after_complete")]
    public async Task Complete_Conflict_Returns409WithStableCode(string code)
    {
        var service = new RecordingAttachmentService
        {
            CompleteResult = new(
                AttachmentCommandStatus.Conflict,
                ErrorCode: code)
        };
        var controller = CreateController(service);

        var result = await controller.Complete(AttachmentId, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(code, problem.Extensions["code"]);
    }

    [Fact]
    public async Task InaccessibleAttachment_IsConcealedAs404AcrossMetadataAndContent()
    {
        var service = new RecordingAttachmentService
        {
            CompleteResult = new(
                AttachmentCommandStatus.Unavailable,
                ErrorCode: "attachment_not_found"),
            StatusResult = null,
            CancelResult = new(
                AttachmentCommandStatus.Unavailable,
                ErrorCode: "attachment_not_found"),
            ContentResult = new(
                AttachmentCommandStatus.Unavailable,
                ErrorCode: "attachment_not_found")
        };
        var controller = CreateController(service);

        var complete = await controller.Complete(AttachmentId, CancellationToken.None);
        var status = await controller.GetStatus(AttachmentId, CancellationToken.None);
        var cancel = await controller.Cancel(AttachmentId, CancellationToken.None);
        var content = await controller.Content(
            AttachmentId,
            cancellationToken: CancellationToken.None);

        Assert.IsType<NotFoundResult>(complete.Result);
        Assert.IsType<NotFoundResult>(status.Result);
        Assert.IsType<NotFoundResult>(cancel);
        Assert.IsType<NotFoundResult>(content);
    }

    [Fact]
    public async Task Content_StreamsOnlySuccessfulReadyAuthorization()
    {
        var bytes = "%PDF-test"u8.ToArray();
        var service = new RecordingAttachmentService
        {
            ContentResult = new(
                AttachmentCommandStatus.Success,
                new AttachmentContentRead(
                    new MemoryStream(bytes),
                    "report.pdf",
                    "application/pdf",
                    "attachment"))
        };
        var controller = CreateController(service);

        var result = await controller.Content(
            AttachmentId,
            "inline",
            CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("report.pdf", file.FileDownloadName);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("inline", service.LastDisposition);
    }

    [Fact]
    public void StatusResponse_SerializesSafeMetadataWithoutStorageFieldsOrSignedUrls()
    {
        var json = JsonSerializer.Serialize(
            CreateStatus(AttachmentStates.Ready),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"displayName\":\"report.pdf\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"Ready\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("quarantine", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("readyKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("etag", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uploadUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", json, StringComparison.OrdinalIgnoreCase);
    }

    private static AttachmentsController CreateController(
        RecordingAttachmentService service)
    {
        var controller = new AttachmentsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateClientPrincipal(42)
                }
            }
        };
        return controller;
    }

    private static ClaimsPrincipal CreateClientPrincipal(long clientId) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "7"),
                new Claim(ClaimTypes.Role, Roles.Client),
                new Claim(CustomClaimTypes.ClientId, clientId.ToString())
            ],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role));

    private static int? GetStatusCode(IActionResult? result) =>
        result switch
        {
            ObjectResult objectResult =>
                objectResult.StatusCode
                ?? (objectResult.Value as ProblemDetails)?.Status,
            StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
            _ => null
        };

    private static AttachmentUploadIntent CreateIntent() =>
        new(
            AttachmentId,
            $"/api/v1/attachments/{AttachmentId:D}/upload",
            DateTimeOffset.UtcNow.AddMinutes(5),
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/pdf"
            },
            new AttachmentSummary(
                AttachmentId,
                "report.pdf",
                "application/pdf",
                100,
                AttachmentStates.PendingUpload,
                null));

    private static AttachmentStatusResponse CreateStatus(
        string status,
        string? rejectionCode = null) =>
        new(
            AttachmentId,
            25,
            "report.pdf",
            "application/pdf",
            100,
            status,
            rejectionCode,
            DateTimeOffset.UtcNow,
            status == AttachmentStates.Ready ? DateTimeOffset.UtcNow : null,
            DateTimeOffset.UtcNow.AddDays(1));

    private sealed class RecordingAttachmentService : IAttachmentService
    {
        public AttachmentCommandResult<AttachmentUploadIntent> CreateResult { get; init; } =
            new(AttachmentCommandStatus.Invalid);
        public AttachmentCommandResult<AttachmentStatusResponse> CompleteResult { get; init; } =
            new(AttachmentCommandStatus.Invalid);
        public AttachmentStatusResponse? StatusResult { get; init; }
        public AttachmentCommandResult<bool> CancelResult { get; init; } =
            new(AttachmentCommandStatus.Invalid);
        public AttachmentCommandResult<StoredObjectInfo> UploadResult { get; init; } =
            new(AttachmentCommandStatus.Invalid);
        public AttachmentCommandResult<AttachmentContentRead> ContentResult { get; init; } =
            new(AttachmentCommandStatus.Invalid);
        public AttachmentActor? LastActor { get; private set; }
        public string? LastDisposition { get; private set; }

        public Task<AttachmentCommandResult<AttachmentUploadIntent>> CreateUploadIntentAsync(
            long conversationId,
            AttachmentActor actor,
            CreateAttachmentUploadRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = actor;
            return Task.FromResult(CreateResult);
        }

        public Task<AttachmentCommandResult<AttachmentStatusResponse>> CompleteAsync(
            Guid attachmentId,
            AttachmentActor actor,
            CancellationToken cancellationToken = default)
        {
            LastActor = actor;
            return Task.FromResult(CompleteResult);
        }

        public Task<AttachmentCommandResult<StoredObjectInfo>> UploadAsync(
            Guid attachmentId,
            AttachmentActor actor,
            Stream content,
            string? mediaType,
            long? contentLength,
            CancellationToken cancellationToken = default)
        {
            LastActor = actor;
            return Task.FromResult(UploadResult);
        }

        public Task<AttachmentStatusResponse?> GetStatusAsync(
            Guid attachmentId,
            AttachmentActor actor,
            CancellationToken cancellationToken = default)
        {
            LastActor = actor;
            return Task.FromResult(StatusResult);
        }

        public Task<AttachmentCommandResult<bool>> CancelAsync(
            Guid attachmentId,
            AttachmentActor actor,
            CancellationToken cancellationToken = default)
        {
            LastActor = actor;
            return Task.FromResult(CancelResult);
        }

        public Task<AttachmentCommandResult<AttachmentContentRead>> OpenContentAsync(
            Guid attachmentId,
            AttachmentActor actor,
            string disposition,
            CancellationToken cancellationToken = default)
        {
            LastActor = actor;
            LastDisposition = disposition;
            return Task.FromResult(ContentResult);
        }
    }
}
