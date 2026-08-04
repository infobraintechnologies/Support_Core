using System.Security.Claims;
using CBSSupport.API.Security;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Controllers;

[ApiController]
[Route("api/v1/tickets")]
public sealed class TicketsController(
    IConversationService conversations,
    IChatService chatService) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = Policies.ClientOnly)]
    public async Task<ActionResult<TicketResponse>> Create(
        CreateTicketRequest request,
        CancellationToken cancellationToken)
    {
        if (!CaseTypes.TryResolveTicket(request.Type, out var typeCode)
            || !CasePriorities.TryNormalize(request.Priority, out var priority))
        {
            return ValidationProblem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "The ticket type or priority is invalid.");
        }

        var result = await conversations.CreateCaseAsync(
            User.GetConversationActor(),
            typeCode,
            InstructionCategories.Ticket,
            request.Description!,
            priority,
            remarks: null,
            expiryDate: null,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken,
            subject: request.Subject);

        return result.Status switch
        {
            ConversationCommandStatus.Created when result.Value is not null =>
                StatusCode(
                    StatusCodes.Status201Created,
                    CaseDtoMapper.ToTicket(result.Value, request.Subject!, priority, request.Description!)),
            ConversationCommandStatus.Unavailable => NotFound(),
            ConversationCommandStatus.Conflict => ConflictProblem(
                result.ErrorCode ?? "case_conflict"),
            _ => ValidationProblem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "The ticket request is invalid.")
        };
    }

    [HttpGet]
    [Authorize(Policy = Policies.ClientOnly)]
    public async Task<ActionResult<IReadOnlyList<TicketResponse>>> List(
        CancellationToken cancellationToken)
    {
        var tickets = await chatService.GetTicketsByClientIdAsync(
            User.GetRequiredClientId(),
            cancellationToken);
        return Ok(tickets.Select(CaseDtoMapper.ToTicket).ToList());
    }

    [HttpGet("{caseId:long}")]
    [Authorize(Policy = Policies.AdminOrClient)]
    public async Task<ActionResult<TicketResponse>> GetDetail(
        long caseId,
        CancellationToken cancellationToken)
    {
        long? scope = User.IsInRole(Roles.Admin) ? null : User.GetRequiredClientId();
        var ticket = await chatService.GetTicketDetailsByIdAsync(caseId, scope, cancellationToken);
        return ticket is null ? NotFound() : Ok(CaseDtoMapper.ToTicket(ticket));
    }

    private ObjectResult ConflictProblem(string code) =>
        Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Ticket conflict",
            detail: "The ticket request conflicts with existing state.",
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

[ApiController]
[Route("api/v1/admin/tickets")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class AdminTicketsController(IChatService chatService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TicketResponse>>> List(
        CancellationToken cancellationToken)
    {
        var tickets = await chatService.GetAllTicketsAsync(cancellationToken);
        return Ok(tickets.Select(CaseDtoMapper.ToTicket).ToList());
    }

    [HttpPut("{caseId:long}/status")]
    public async Task<ActionResult<TicketResponse>> UpdateStatus(
        long caseId,
        UpdateCaseStatusRequest request,
        CancellationToken cancellationToken)
    {
        var current = await chatService.GetTicketDetailsByIdAsync(caseId, null, cancellationToken);
        if (current is null)
        {
            return NotFound();
        }

        if (!TryParseStatus(request.Status, out var isCompleted, out var targetLabel))
        {
            return ValidationProblem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "The ticket status must be 'Open' or 'Resolved'.");
        }

        if (string.Equals(current.Status, targetLabel, StringComparison.OrdinalIgnoreCase))
        {
            return ConflictProblem("invalid_status_transition");
        }

        if (!await chatService.UpdateTicketStatusAsync(
                caseId,
                isCompleted,
                User.GetRequiredUserId(),
                cancellationToken))
        {
            return NotFound();
        }

        var updated = await chatService.GetTicketDetailsByIdAsync(caseId, null, cancellationToken);
        return updated is null ? NotFound() : Ok(CaseDtoMapper.ToTicket(updated));
    }

    private ObjectResult ConflictProblem(string code) =>
        Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Ticket conflict",
            detail: "The requested status transition is invalid.",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static bool TryParseStatus(string? status, out bool isCompleted, out string targetLabel)
    {
        if (string.Equals(status, CaseDtoMapper.TicketResolvedStatus, StringComparison.OrdinalIgnoreCase))
        {
            isCompleted = true;
            targetLabel = CaseDtoMapper.TicketResolvedStatus;
            return true;
        }

        if (string.Equals(status, CaseDtoMapper.TicketOpenStatus, StringComparison.OrdinalIgnoreCase))
        {
            isCompleted = false;
            targetLabel = CaseDtoMapper.TicketOpenStatus;
            return true;
        }

        isCompleted = false;
        targetLabel = string.Empty;
        return false;
    }
}
