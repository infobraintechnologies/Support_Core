using CBSSupport.API.Hubs;
using CBSSupport.API.Security;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace CBSSupport.API.Controllers;

[ApiController]
[Route("v1/api/instructions")]
[Authorize(Policy = Policies.AdminOrClient)]
public sealed class InstructionsController : ControllerBase
{
    private readonly IChatService _service;
    private readonly IConversationService _conversations;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHubContext<ChatHub> _hubContext;

    public InstructionsController(
        IChatService service,
        IConversationService conversations,
        IAuthorizationService authorizationService,
        IHubContext<ChatHub> hubContext)
    {
        _service = service;
        _conversations = conversations;
        _authorizationService = authorizationService;
        _hubContext = hubContext;
    }

    [HttpPost("support-group")]
    public IActionResult SaveSupportGroupChat(CreateInstructionRequest request) => NotFound();

    [HttpPost("clients/{clientId:long}/support-group")]
    [Authorize(Policy = Policies.AdminOnly)]
    public IActionResult SaveAdminSupportGroupChat(
        long clientId,
        CreateInstructionRequest request) => NotFound();

    [HttpPost("support-private")]
    [Authorize(Policy = Policies.ClientOnly)]
    public IActionResult SaveSupportPrivateChat(CreateInstructionRequest request) =>
        NotFound();

    [HttpPost("internal-team-chat")]
    [Authorize(Policy = Policies.AdminOnly)]
    public Task<IActionResult> SaveInternalTeamChat(CreateInstructionRequest request) =>
        SaveInstructionAsync(request, 105, 100, null);

    [HttpPost("ticket/training")]
    [Authorize(Policy = Policies.ClientOnly)]
    public Task<IActionResult> SaveTicketTraining(CreateInstructionRequest request) =>
        SaveClientInstructionAsync(request, 110, 101);

    [HttpPost("ticket/migration")]
    [Authorize(Policy = Policies.ClientOnly)]
    public Task<IActionResult> SaveMigrationTicket(CreateInstructionRequest request) =>
        SaveClientInstructionAsync(request, 111, 101);

    [HttpPost("ticket/setup")]
    [Authorize(Policy = Policies.ClientOnly)]
    public Task<IActionResult> SaveSetupTicket(CreateInstructionRequest request) =>
        SaveClientInstructionAsync(request, 112, 101);

    [HttpPost("ticket/correction")]
    [Authorize(Policy = Policies.ClientOnly)]
    public Task<IActionResult> SaveCorrectionTicket(CreateInstructionRequest request) =>
        SaveClientInstructionAsync(request, 113, 101);

    [HttpPost("ticket/bug-fix")]
    [Authorize(Policy = Policies.ClientOnly)]
    public Task<IActionResult> SaveBugFixTicket(CreateInstructionRequest request) =>
        SaveClientInstructionAsync(request, 114, 101);

    [HttpPost("ticket/new-feature")]
    [Authorize(Policy = Policies.ClientOnly)]
    public Task<IActionResult> SaveNewFeatureTicket(CreateInstructionRequest request) =>
        SaveClientInstructionAsync(request, 115, 101);

    [HttpPost("ticket/feature-enhancement")]
    [Authorize(Policy = Policies.ClientOnly)]
    public Task<IActionResult> SaveFeatureEnhancementTicket(CreateInstructionRequest request) =>
        SaveClientInstructionAsync(request, 116, 101);

    [HttpPost("ticket/backend-workaround")]
    [Authorize(Policy = Policies.ClientOnly)]
    public Task<IActionResult> SaveBackendWorkaroundTicket(CreateInstructionRequest request) =>
        SaveClientInstructionAsync(request, 117, 101);

    [HttpPost("inquiry/accounts")]
    [Authorize(Policy = Policies.ClientOnly)]
    public Task<IActionResult> SaveAccountsInquiry(CreateInstructionRequest request) =>
        SaveClientInstructionAsync(request, 121, 102);

    [HttpPost("inquiry/sales")]
    [Authorize(Policy = Policies.ClientOnly)]
    public Task<IActionResult> SaveSalesInquiry(CreateInstructionRequest request) =>
        SaveClientInstructionAsync(request, 122, 102);

    [HttpPost("reply")]
    public Task<IActionResult> SaveReply(CreateInstructionRequest request) => SaveReplyAsync(request);

    [HttpGet("by-type/{*chatType}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> GetConversationsByChatType(string chatType)
    {
        if (!TryGetInstructionType(chatType, out var instructionTypeId))
        {
            return BadRequest(new { message = "Invalid chat type specified." });
        }

        if (instructionTypeId == ConversationTypes.SupportPrivate)
        {
            return NotFound();
        }

        var conversations = await _service.GetConversationsByInstTypeAsync(instructionTypeId);
        return Ok(conversations);
    }

    [HttpGet("messages/{conversationId:long}")]
    public async Task<IActionResult> GetMessagesForConversation(long conversationId)
    {
        if (conversationId <= 0)
        {
            return BadRequest(new { message = "A valid conversation ID must be provided." });
        }


        var root = await _service.GetInstructionByIdAsync(conversationId);
        if (root is null || root.InstTypeId == ConversationTypes.SupportPrivate)
        {
            return NotFound();
        }

        var messages = (await _service.GetMessagesByConversationIdAsync(
            conversationId,
            GetClientScope())).ToList();

        return messages.Count == 0 ? NotFound() : Ok(messages);
    }

    [HttpGet("sidebar/{clientId:long}")]
    public async Task<IActionResult> GetSidebar(long clientId)
    {
        if (!await CanAccessTenantAsync(clientId))
        {
            return NotFound();
        }

        var sidebar = await _service.GetSidebarForUserAsync(0, clientId);
        sidebar.PrivateChats.Clear();
        return Ok(sidebar);
    }

    [HttpGet("tickets/{clientId:long}")]
    public async Task<IActionResult> GetTicketsForClient(long clientId)
    {
        if (!await CanAccessTenantAsync(clientId))
        {
            return NotFound();
        }

        var tickets = await _service.GetTicketsByClientIdAsync(clientId);
        return Ok(new { data = tickets });
    }

    [HttpGet("tickets")]
    [Authorize(Policy = Policies.ClientOnly)]
    public async Task<IActionResult> GetTicketsForCurrentClient()
    {
        var tickets = await _service.GetTicketsByClientIdAsync(User.GetRequiredClientId());
        return Ok(new { data = tickets });
    }

    [HttpGet("inquiries/{clientId:long}")]
    public async Task<IActionResult> GetInquiriesForClient(long clientId)
    {
        if (!await CanAccessTenantAsync(clientId))
        {
            return NotFound();
        }

        var inquiries = await _service.GetInquiriesByClientIdAsync(clientId);
        return Ok(new { data = inquiries });
    }

    [HttpGet("inquiries")]
    [Authorize(Policy = Policies.ClientOnly)]
    public async Task<IActionResult> GetInquiriesForCurrentClient()
    {
        var inquiries = await _service.GetInquiriesByClientIdAsync(User.GetRequiredClientId());
        return Ok(new { data = inquiries });
    }

    [HttpGet("tickets/all")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> GetAllTickets() =>
        Ok(new { data = await _service.GetAllTicketsAsync() });

    [HttpGet("inquiries/all")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> GetAllInquiries() =>
        Ok(new { data = await _service.GetAllInquiriesAsync() });

    [HttpPut("update/{ticketId:long}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> UpdateTicket(
        long ticketId,
        UpdateInstructionRequest request)
    {
        var existingTicket = await _service.GetInstructionByIdAsync(ticketId);
        if (existingTicket is null)
        {
            return NotFound();
        }

        if (existingTicket.Completed is true)
        {
            return Conflict(new { message = "Cannot edit a resolved ticket." });
        }

        var remarks = System.Text.Json.JsonSerializer.Serialize(new
        {
            priority = request.Priority ?? "Normal",
            userremarks = request.Remarks ?? string.Empty
        });

        var updatedTicket = new ChatMessage
        {
            Id = ticketId,
            Instruction = request.Instruction,
            Remarks = remarks,
            ExpiryDate = request.ExpiryDate,
            EditDate = DateTime.UtcNow,
            EditUser = User.GetRequiredUserId()
        };

        return await _service.UpdateInstructionAsync(updatedTicket)
            ? Ok(new { message = "Ticket updated successfully." })
            : NotFound();
    }

    [HttpGet("tickets/{ticketId:long}/details")]
    public async Task<IActionResult> GetTicketDetails(long ticketId)
    {
        var ticket = await _service.GetTicketDetailsByIdAsync(ticketId, GetClientScope());
        return ticket is null ? NotFound() : Ok(ticket);
    }

    [HttpGet("inquiries/{inquiryId:long}/details")]
    public async Task<IActionResult> GetInquiryDetails(long inquiryId)
    {
        var inquiry = await _service.GetInquiryDetailsByIdAsync(inquiryId, GetClientScope());
        return inquiry is null ? NotFound() : Ok(inquiry);
    }

    [HttpPut("tickets/{ticketId:long}/status")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> UpdateTicketStatus(
        long ticketId,
        UpdateStatusRequest request)
    {
        var userId = User.GetRequiredUserId();
        if (!await _service.UpdateTicketStatusAsync(ticketId, request.IsCompleted, userId))
        {
            return NotFound();
        }

        var ticket = await _service.GetTicketDetailsByIdAsync(ticketId);
        if (ticket is not null)
        {
            await _hubContext.Clients.Group(RealtimeGroupNames.Tenant(ticket.ClientId)).SendAsync(
                "TicketStatusUpdated",
                new
                {
                    TicketId = ticketId,
                    NewStatus = request.IsCompleted ? "Resolved" : "Open",
                    UpdatedAt = DateTime.UtcNow
                },
                HttpContext.RequestAborted);
        }

        return Ok(new { success = true, message = "Ticket status updated successfully." });
    }

    [HttpPut("inquiries/{inquiryId:long}/status")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> UpdateInquiryStatus(
        long inquiryId,
        UpdateStatusRequest request)
    {
        var userId = User.GetRequiredUserId();
        if (!await _service.UpdateInquiryStatusAsync(inquiryId, request.IsCompleted, userId))
        {
            return NotFound();
        }

        var inquiry = await _service.GetInquiryDetailsByIdAsync(inquiryId);
        if (inquiry is not null)
        {
            await _hubContext.Clients.Group(RealtimeGroupNames.Tenant(inquiry.ClientId)).SendAsync(
                "InquiryStatusUpdated",
                new
                {
                    InquiryId = inquiryId,
                    NewStatus = request.IsCompleted ? "Completed" : "Pending",
                    UpdatedAt = DateTime.UtcNow
                },
                HttpContext.RequestAborted);
        }

        return Ok(new { success = true, message = "Inquiry status updated successfully." });
    }

    [HttpGet("notifications/unread")]
    public async Task<IActionResult> GetUnreadNotifications()
    {
        if (User.IsInRole(Roles.Admin))
        {
            return Ok(await _service.GetUnreadNotificationsForAdminAsync());
        }

        return Ok(await _service.GetUnreadNotificationsForClientAsync(User.GetRequiredClientId()));
    }

    [HttpPut("{instructionId:long}/mark-seen-admin")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> MarkNotificationSeenByAdmin(long instructionId) =>
        await _service.MarkNotificationSeenByAdminAsync(instructionId)
            ? Ok(new { success = true, message = "Notification marked as seen." })
            : NotFound();

    [HttpPut("mark-all-seen-client")]
    [Authorize(Policy = Policies.ClientOnly)]
    public async Task<IActionResult> MarkAllNotificationsSeenByClient()
    {
        var count = await _service.MarkAllNotificationsSeenByClientAsync(User.GetRequiredClientId());
        return Ok(new { success = true, message = "All notifications marked as seen by client.", count });
    }

    [HttpPut("mark-all-seen-admin")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> MarkAllNotificationsSeenByAdmin()
    {
        var count = await _service.MarkAllNotificationsSeenByAdminAsync();
        return Ok(new { success = true, message = "All notifications marked as seen.", count });
    }

    [HttpPut("{instructionId:long}/mark-seen-client")]
    [Authorize(Policy = Policies.ClientOnly)]
    public async Task<IActionResult> MarkNotificationSeenByClient(long instructionId) =>
        await _service.MarkNotificationSeenByClientAsync(instructionId, User.GetRequiredClientId())
            ? Ok(new { success = true, message = "Notification marked as seen by client." })
            : NotFound();

    private Task<IActionResult> SaveClientInstructionAsync(
        CreateInstructionRequest request,
        short instructionTypeId,
        short instructionCategoryId) =>
        SaveInstructionAsync(
            request with { InstructionId = null },
            instructionTypeId,
            instructionCategoryId,
            User.GetRequiredClientId());

    private async Task<IActionResult> SaveReplyAsync(CreateInstructionRequest request)
    {
        if (request.InstructionId is not > 0)
        {
            return BadRequest(new { message = "A valid conversation ID is required." });
        }

        var actor = GetRequiredConversationActor();
        var access = await _conversations.GetAccessAsync(
            request.InstructionId.Value,
            actor,
            HttpContext.RequestAborted);
        if (access is null || !access.IsCase)
        {
            return NotFound();
        }

        var result = await _conversations.SendMessageAsync(
            request.InstructionId.Value,
            actor,
            Guid.NewGuid(),
            request.Instruction,
            [],
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            HttpContext.RequestAborted);
        if (result.Status is ConversationCommandStatus.Unavailable)
        {
            return NotFound();
        }
        if (result.Status is not ConversationCommandStatus.Created || result.Value is null)
        {
            return result.Status is ConversationCommandStatus.Conflict
                ? Conflict(new { code = result.ErrorCode ?? "message_conflict" })
                : ValidationProblem("Message text is required.");
        }

        var message = result.Value;
        return Ok(new
        {
            message.Id,
            InstructionId = message.ConversationId,
            Instruction = message.Text,
            DateTime = message.SentAt,
            InsertUser = actor.IsAdmin ? checked((int)actor.UserId) : (int?)null,
            ClientAuthUserId = actor.IsAdmin ? (int?)null : checked((int)actor.UserId),
            SenderName = actor.DisplayName,
            ClientId = access.ClientId,
            InstTypeId = access.InstructionTypeId,
            InstCategoryId = access.InstructionCategoryId,
            message.ClientMessageId,
            ConversationSequence = message.Sequence,
            Attachments = Array.Empty<AttachmentSummary>()
        });
    }

    private async Task<IActionResult> SaveInstructionAsync(
        CreateInstructionRequest request,
        short instructionTypeId,
        short instructionCategoryId,
        long? clientId)
    {
        if (ConversationTypes.IsCase(instructionTypeId))
        {
            var result = await _conversations.CreateCaseAsync(
                GetRequiredConversationActor(),
                instructionTypeId,
                instructionCategoryId,
                request.Instruction,
                request.Priority,
                request.Remarks,
                request.ExpiryDate,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                HttpContext.RequestAborted);
            return result.Status switch
            {
                ConversationCommandStatus.Created when result.Value is not null =>
                    Ok(result.Value),
                ConversationCommandStatus.Unavailable => NotFound(),
                ConversationCommandStatus.Conflict =>
                    Conflict(new { code = result.ErrorCode ?? "case_conflict" }),
                _ => ValidationProblem("The ticket or inquiry request is invalid.")
            };
        }

        var userId = User.GetRequiredUserId();
        var isClient = User.IsInRole(Roles.Client);
        var now = DateTime.UtcNow;

        var instruction = new ChatMessage
        {
            Instruction = request.Instruction.Trim(),
            InstructionId = request.InstructionId is > 0 ? request.InstructionId : null,
            Priority = request.Priority,
            Remarks = request.Remarks,
            ExpiryDate = request.ExpiryDate,
            InstTypeId = instructionTypeId,
            InstCategoryId = instructionCategoryId,
            DateTime = now,
            InsertDate = now,
            Status = true,
            InstChannel = "chat",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            InsertUser = isClient ? null : checked((int)userId),
            UserId = isClient ? null : checked((int)userId),
            ClientId = clientId,
            ClientAuthUserId = isClient ? checked((int)userId) : null,
            ClientUserId = null,
            ServiceId = 3
        };

        var savedInstruction = await _service.CreateInstructionTicketAsync(
            instruction,
            HttpContext.RequestAborted);
        if (savedInstruction is not null)
        {
            await PublishCommittedInstructionAsync(
                savedInstruction,
                instructionCategoryId,
                request.InstructionId);
            return Ok(savedInstruction);
        }

        return request.InstructionId is > 0
            ? NotFound()
            : Problem(statusCode: StatusCodes.Status500InternalServerError);
    }

    private async Task PublishCommittedInstructionAsync(
        ChatMessage savedInstruction,
        short instructionCategoryId,
        long? requestedConversationId)
    {
        if (requestedConversationId is > 0)
        {
            var userId = User.GetRequiredUserId();
            var isAdmin = User.IsInRole(Roles.Admin);
            var message = new ConversationMessage(
                savedInstruction.Id,
                requestedConversationId.Value,
                savedInstruction.Instruction ?? string.Empty,
                savedInstruction.DateTime,
                new ConversationSender(
                    userId,
                    User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? $"User {userId}",
                    isAdmin ? Roles.Admin : Roles.Client));

            await _hubContext.Clients
                .Group(RealtimeGroupNames.Conversation(requestedConversationId.Value))
                .SendAsync("MessageCreated", message, HttpContext.RequestAborted);
            return;
        }

        var eventName = instructionCategoryId switch
        {
            101 => "TicketChanged",
            102 => "InquiryChanged",
            _ => null
        };
        if (eventName is null)
        {
            return;
        }

        await _hubContext.Clients.Group(RealtimeGroupNames.Admins).SendAsync(
            eventName,
            new
            {
                savedInstruction.Id,
                savedInstruction.ClientId,
                savedInstruction.InstTypeId,
                savedInstruction.DateTime
            },
            HttpContext.RequestAborted);
    }

    private long? GetClientScope() =>
        User.IsInRole(Roles.Admin) ? null : User.GetRequiredClientId();

    private ConversationActor GetRequiredConversationActor()
    {
        var userId = User.GetRequiredUserId();
        var isAdmin = User.IsInRole(Roles.Admin);
        return new ConversationActor(
            userId,
            isAdmin ? null : User.GetRequiredClientId(),
            isAdmin,
            User.FindFirstValue(ClaimTypes.Name)
                ?? User.Identity?.Name
                ?? $"User {userId}");
    }

    private async Task<bool> CanAccessTenantAsync(long clientId)
    {
        var result = await _authorizationService.AuthorizeAsync(
            User,
            new TenantResource(clientId),
            TenantAccessRequirement.Instance);
        return result.Succeeded;
    }

    private static bool TryGetInstructionType(string chatType, out short instructionTypeId)
    {
        instructionTypeId = chatType.ToLowerInvariant() switch
        {
            "support-group" => 100,
            "support-private" => 101,
            "internal-team-chat" => 105,
            "ticket/training" => 110,
            "ticket/migration" => 111,
            "ticket/setup" => 112,
            "ticket/correction" => 113,
            "ticket/bug-fix" => 114,
            "ticket/new-feature" => 115,
            "ticket/feature-enhancement" => 116,
            "ticket/backend-workaround" => 117,
            "inquiry/accounts" => 121,
            "inquiry/sales" => 122,
            _ => 0
        };

        return instructionTypeId != 0;
    }
}
