using System.Security.Claims;
using CBSSupport.API.Security;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Controllers;

[ApiController]
[Route("api/v1/inquiries")]
public sealed class InquiriesController(
    IConversationService conversations,
    IChatService chatService) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = Policies.ClientOnly)]
    public async Task<ActionResult<InquiryResponse>> Create(
        CreateInquiryRequest request,
        CancellationToken cancellationToken)
    {
        if (!CaseTypes.TryResolveInquiry(request.Type, out var typeCode)
            || !CasePriorities.TryNormalize(request.Priority, out var priority))
        {
            return ValidationProblem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "The inquiry type or priority is invalid.");
        }

        var result = await conversations.CreateCaseAsync(
            User.GetConversationActor(),
            typeCode,
            InstructionCategories.Inquiry,
            request.Description!,
            priority,
            remarks: request.Topic,
            expiryDate: null,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken,
            subject: null);

        return result.Status switch
        {
            ConversationCommandStatus.Created when result.Value is not null =>
                StatusCode(
                    StatusCodes.Status201Created,
                    CaseDtoMapper.ToInquiry(result.Value, request.Topic!, priority, request.Description!)),
            ConversationCommandStatus.Unavailable => NotFound(),
            ConversationCommandStatus.Conflict => ConflictProblem(
                result.ErrorCode ?? "case_conflict"),
            _ => ValidationProblem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "The inquiry request is invalid.")
        };
    }

    [HttpGet]
    [Authorize(Policy = Policies.ClientOnly)]
    public async Task<ActionResult<CasePage<InquiryResponse>>> List(
        [FromQuery] CaseListQuery query,
        CancellationToken cancellationToken)
    {
        if (!CasePagination.TryCreateInquiryCriteria(query, allowClientFilter: false, out var criteria, out var error))
        {
            return ValidationProblem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: error);
        }

        var page = await chatService.ListInquiriesAsync(
            criteria! with { ClientId = User.GetRequiredClientId() },
            cancellationToken);
        return Ok(new CasePage<InquiryResponse>(
            page.Items.Select(CaseDtoMapper.ToInquiry).ToArray(),
            page.PageSize,
            page.NextCursor));
    }

    [HttpGet("{caseId:long}")]
    [Authorize(Policy = Policies.AdminOrClient)]
    public async Task<ActionResult<InquiryResponse>> GetDetail(
        long caseId,
        CancellationToken cancellationToken)
    {
        long? scope = User.IsInRole(Roles.Admin) ? null : User.GetRequiredClientId();
        var inquiry = await chatService.GetInquiryDetailsByIdAsync(caseId, scope, cancellationToken);
        return inquiry is null ? NotFound() : Ok(CaseDtoMapper.ToInquiry(inquiry));
    }

    private ObjectResult ConflictProblem(string code) =>
        Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Inquiry conflict",
            detail: "The inquiry request conflicts with existing state.",
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

[ApiController]
[Route("api/v1/admin/inquiries")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class AdminInquiriesController(IChatService chatService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CasePage<InquiryResponse>>> List(
        [FromQuery] CaseListQuery query,
        CancellationToken cancellationToken)
    {
        if (!CasePagination.TryCreateInquiryCriteria(query, allowClientFilter: true, out var criteria, out var error))
        {
            return ValidationProblem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: error);
        }

        var page = await chatService.ListInquiriesAsync(criteria!, cancellationToken);
        return Ok(new CasePage<InquiryResponse>(
            page.Items.Select(CaseDtoMapper.ToInquiry).ToArray(),
            page.PageSize,
            page.NextCursor));
    }

    [HttpPut("{caseId:long}/status")]
    public async Task<ActionResult<InquiryResponse>> UpdateStatus(
        long caseId,
        UpdateCaseStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseStatus(request.Status, out var isCompleted, out _))
        {
            return ValidationProblem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "The inquiry status must be 'Pending' or 'Completed'.");
        }

        var mutation = await chatService.UpdateInquiryStatusAsync(
            caseId, isCompleted, User.GetRequiredUserId(), request.ExpectedVersion, cancellationToken);
        if (mutation.Status == CaseMutationStatus.NotFound)
            return NotFound();
        if (mutation.Status == CaseMutationStatus.Conflict)
            return ConflictProblem("inquiry_version_conflict");
        if (mutation.Status == CaseMutationStatus.InvalidState)
            return ConflictProblem("invalid_status_transition");

        var updated = await chatService.GetInquiryDetailsByIdAsync(caseId, null, cancellationToken);
        return updated is null ? NotFound() : Ok(CaseDtoMapper.ToInquiry(updated));
    }

    private ObjectResult ConflictProblem(string code) =>
        Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Inquiry conflict",
            detail: "The inquiry was changed by another request or the transition is no longer valid.",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static bool TryParseStatus(string? status, out bool isCompleted, out string targetLabel)
    {
        if (string.Equals(status, CaseDtoMapper.InquiryCompletedStatus, StringComparison.OrdinalIgnoreCase))
        {
            isCompleted = true;
            targetLabel = CaseDtoMapper.InquiryCompletedStatus;
            return true;
        }

        if (string.Equals(status, CaseDtoMapper.InquiryPendingStatus, StringComparison.OrdinalIgnoreCase))
        {
            isCompleted = false;
            targetLabel = CaseDtoMapper.InquiryPendingStatus;
            return true;
        }

        isCompleted = false;
        targetLabel = string.Empty;
        return false;
    }
}
