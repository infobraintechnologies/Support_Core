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
    IChatService chatService,
    IAuthorizationService? authorizationService = null) : ControllerBase
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
    public async Task<ActionResult<CasePage<TicketResponse>>> List(
        [FromQuery] CaseListQuery query,
        CancellationToken cancellationToken)
    {
        if (!CasePagination.TryCreateTicketCriteria(query, allowClientFilter: false, out var criteria, out var error))
        {
            return ValidationProblem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: error);
        }

        var page = await chatService.ListTicketsAsync(
            criteria! with { ClientId = User.GetRequiredClientId() },
            cancellationToken);
        return Ok(new CasePage<TicketResponse>(
            page.Items.Select(CaseDtoMapper.ToTicket).ToArray(),
            page.PageSize,
            page.NextCursor));
    }

    [HttpGet("{caseId:long}")]
    [Authorize(Policy = Policies.AdminOrClient)]
    public async Task<ActionResult<TicketResponse>> GetDetail(
        long caseId,
        CancellationToken cancellationToken)
    {
        long? scope = User.IsInRole(Roles.Admin) ? null : User.GetRequiredClientId();
        var ticket = await chatService.GetTicketDetailsByIdAsync(caseId, scope, cancellationToken);
        if (ticket is null
            || (User.IsInRole(Roles.Admin)
                && !await CanAccessTenantAsync(ticket.ClientId, cancellationToken)))
        {
            return NotFound();
        }

        return Ok(CaseDtoMapper.ToTicket(ticket));
    }

    private async Task<bool> CanAccessTenantAsync(long clientId, CancellationToken cancellationToken)
    {
        if (authorizationService is not null)
        {
            return (await authorizationService.AuthorizeAsync(
                User,
                new TenantResource(clientId),
                TenantAccessRequirement.Instance)).Succeeded;
        }

        var context = new AuthorizationHandlerContext(
            [TenantAccessRequirement.Instance],
            User,
            new TenantResource(clientId));
        await new TenantAccessHandler().HandleAsync(context);
        return context.HasSucceeded;
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
public sealed class AdminTicketsController(
    IChatService chatService,
    ITicketService tickets,
    IAuthorizationService? authorizationService = null) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CasePage<TicketResponse>>> List(
        [FromQuery] CaseListQuery query,
        CancellationToken cancellationToken)
    {
        if (!CasePagination.TryCreateTicketCriteria(query, allowClientFilter: true, out var criteria, out var error))
        {
            return ValidationProblem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: error);
        }

        if (criteria!.ClientId is long selectedClientId
            && !await CanAccessTenantAsync(selectedClientId, cancellationToken))
        {
            return NotFound();
        }

        var page = await chatService.ListTicketsAsync(criteria!, cancellationToken);
        return Ok(new CasePage<TicketResponse>(
            page.Items.Select(CaseDtoMapper.ToTicket).ToArray(),
            page.PageSize,
            page.NextCursor));
    }

    [HttpPut("{caseId:long}/status")]
    public async Task<ActionResult<TicketResponse>> UpdateStatus(
        long caseId,
        UpdateCaseStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseStatus(request.Status, out var isCompleted, out _))
        {
            return ValidationProblem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "The ticket status must be 'Open' or 'Resolved'.");
        }

        var current = await chatService.GetTicketDetailsByIdAsync(caseId, null, cancellationToken);
        if (current is null || !await CanAccessTenantAsync(current.ClientId, cancellationToken))
        {
            return NotFound();
        }

        var mutation = await tickets.UpdateStatusAsync(
            new CaseStatusUpdateCommand(caseId, isCompleted, User.GetRequiredUserId(), request.ExpectedVersion),
            cancellationToken);
        if (mutation.Status == CaseMutationStatus.NotFound)
            return NotFound();
        if (mutation.Status == CaseMutationStatus.Conflict)
            return ConflictProblem("ticket_version_conflict");
        if (mutation.Status == CaseMutationStatus.InvalidState)
            return ConflictProblem("invalid_status_transition");

        var updated = await chatService.GetTicketDetailsByIdAsync(caseId, null, cancellationToken);
        return updated is null ? NotFound() : Ok(CaseDtoMapper.ToTicket(updated));
    }

    private ObjectResult ConflictProblem(string code) =>
        Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Ticket conflict",
            detail: "The ticket was changed by another request or the transition is no longer valid.",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private async Task<bool> CanAccessTenantAsync(long clientId, CancellationToken cancellationToken)
    {
        if (authorizationService is not null)
        {
            return (await authorizationService.AuthorizeAsync(
                User,
                new TenantResource(clientId),
                TenantAccessRequirement.Instance)).Succeeded;
        }

        var context = new AuthorizationHandlerContext(
            [TenantAccessRequirement.Instance],
            User,
            new TenantResource(clientId));
        await new TenantAccessHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

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
