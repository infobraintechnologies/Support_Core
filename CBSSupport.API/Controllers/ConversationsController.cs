using System.Security.Claims;
using CBSSupport.API.Security;
using CBSSupport.API.Configuration;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CBSSupport.API.Controllers;

[ApiController]
[Route("api/v1/conversations")]
[Authorize(Policy = Policies.AdminOrClient)]
public sealed class ConversationsController(
    IConversationService conversations,
    IOptions<MessagingFeatureOptions> featureOptions) : ControllerBase
{
    private readonly MessagingFeatureOptions _features = featureOptions.Value;

    [HttpGet]
    public async Task<ActionResult<ConversationPage<ConversationSummary>>> List(
        [FromQuery] int limit = 50,
        [FromQuery] long? beforeConversationId = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100 || beforeConversationId is <= 0)
        {
            return ValidationProblem("Pagination parameters are invalid.");
        }

        var items = await conversations.ListAsync(
            GetRequiredActor(),
            limit,
            beforeConversationId,
            cancellationToken);
        var enabledItems = items
            .Where(item => item.Kind switch
            {
                ConversationKinds.Ticket or ConversationKinds.Inquiry => true,
                ConversationKinds.Group => _features.GroupEnabled,
                ConversationKinds.Private => _features.PrivateEnabled,
                _ => false
            })
            .ToArray();
        long? next = items.Count == limit ? items[^1].Id : null;
        return Ok(new ConversationPage<ConversationSummary>(enabledItems, next));
    }

    [HttpPost("group")]
    [Authorize(Policy = Policies.ClientOnly)]
    [EnableRateLimiting(MessagingRateLimitPolicies.ConversationCreation)]
    public async Task<ActionResult<ConversationSummary>> GetOrCreateGroup(
        CancellationToken cancellationToken)
    {
        if (!_features.GroupEnabled)
        {
            return NotFound();
        }

        var result = await conversations.GetOrCreateGroupAsync(
            GetRequiredActor(),
            adminSelectedClientId: null,
            cancellationToken);
        return ToCreateResult(result);
    }

    [HttpPost("private")]
    [EnableRateLimiting(MessagingRateLimitPolicies.ConversationCreation)]
    public async Task<ActionResult<ConversationSummary>> GetOrCreatePrivate(
        CreatePrivateConversationRequest request,
        CancellationToken cancellationToken)
    {
        if (!_features.PrivateEnabled)
        {
            return NotFound();
        }

        var result = await conversations.GetOrCreatePrivateAsync(
            GetRequiredActor(),
            request.CounterpartyUserId,
            cancellationToken);
        return ToCreateResult(result);
    }

    [HttpGet("available-admins")]
    public async Task<ActionResult<IReadOnlyList<ConversationDirectoryUser>>> GetAvailableAdmins(
        CancellationToken cancellationToken)
    {
        if (!_features.PrivateEnabled)
        {
            return NotFound();
        }

        return Ok(await conversations.GetAvailableAdminsAsync(GetRequiredActor(), cancellationToken));
    }

    [HttpGet("{conversationId:long}/messages")]
    public async Task<ActionResult<ConversationPage<ConversationMessage>>> GetMessages(
        long conversationId,
        [FromQuery] int limit = 50,
        [FromQuery] long? beforeSequence = null,
        [FromQuery] long? afterSequence = null,
        CancellationToken cancellationToken = default)
    {
        var actor = GetRequiredActor();
        if (!await IsConversationFeatureEnabledAsync(conversationId, actor, cancellationToken))
        {
            return NotFound();
        }

        if (conversationId <= 0
            || limit is < 1 or > 100
            || (beforeSequence.HasValue && afterSequence.HasValue)
            || beforeSequence is < 1
            || afterSequence is < 0)
        {
            return ValidationProblem("Message pagination parameters are invalid.");
        }

        var page = await conversations.GetMessagesAsync(
            conversationId,
            actor,
            limit,
            beforeSequence,
            afterSequence,
            cancellationToken);
        return page is null ? NotFound() : Ok(page);
    }

    [HttpPost("{conversationId:long}/messages")]
    [EnableRateLimiting(MessagingRateLimitPolicies.MessageSend)]
    public async Task<ActionResult<ConversationMessage>> SendMessage(
        long conversationId,
        SendMessageV2Request request,
        CancellationToken cancellationToken)
    {
        var actor = GetRequiredActor();
        if (!await IsConversationFeatureEnabledAsync(conversationId, actor, cancellationToken))
        {
            return NotFound();
        }

        var result = await conversations.SendMessageAsync(
            conversationId,
            actor,
            request.ClientMessageId,
            request.Text,
            request.AttachmentIds ?? [],
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return result.Status switch
        {
            ConversationCommandStatus.Created => StatusCode(
                StatusCodes.Status201Created,
                result.Value),
            ConversationCommandStatus.Replayed => Ok(result.Value),
            ConversationCommandStatus.Unavailable => NotFound(),
            // Do not disclose whether the UUID belongs to another actor/conversation.
            ConversationCommandStatus.Conflict => ConflictProblem(
                result.ErrorCode ?? "message_conflict"),
            _ => ValidationProblem("A clientMessageId and text or attachments are required.")
        };
    }

    [HttpPut("{conversationId:long}/read")]
    public async Task<IActionResult> AdvanceRead(
        long conversationId,
        AdvanceConversationReadRequest request,
        CancellationToken cancellationToken)
    {
        var actor = GetRequiredActor();
        if (!await IsConversationFeatureEnabledAsync(conversationId, actor, cancellationToken))
        {
            return NotFound();
        }

        var result = await conversations.AdvanceReadCursorAsync(
            conversationId,
            actor,
            request.ThroughSequence,
            cancellationToken);
        return result.Status switch
        {
            ConversationCommandStatus.Success => NoContent(),
            ConversationCommandStatus.Unavailable => NotFound(),
            ConversationCommandStatus.Conflict => ConflictProblem(
                result.ErrorCode ?? "read_cursor_conflict"),
            _ => ValidationProblem("The read cursor is invalid.")
        };
    }

    [HttpPut("{conversationId:long}/assignment")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<ActionResult<ConversationSummary>> Transfer(
        long conversationId,
        TransferConversationRequest request,
        CancellationToken cancellationToken)
    {
        if (!_features.PrivateEnabled)
        {
            return NotFound();
        }

        var result = await conversations.TransferAsync(
            conversationId,
            GetRequiredActor(),
            request.AdminUserId,
            request.ExpectedVersion,
            request.Reason,
            cancellationToken);
        return ToMutationResult(result);
    }

    [HttpPut("{conversationId:long}/archive")]
    public async Task<ActionResult<ConversationSummary>> Archive(
        long conversationId,
        ArchiveConversationRequest request,
        CancellationToken cancellationToken)
    {
        if (!_features.PrivateEnabled)
        {
            return NotFound();
        }

        var result = await conversations.ArchiveAsync(
            conversationId,
            GetRequiredActor(),
            request.ExpectedVersion,
            cancellationToken);
        return ToMutationResult(result);
    }

    [HttpPut("{conversationId:long}/review")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<ActionResult<ConversationSummary>> ApproveLegacyPrivate(
        long conversationId,
        ReviewPrivateConversationRequest request,
        CancellationToken cancellationToken)
    {
        // Deliberately available while MessagingV2Private is disabled so operators
        // can resolve every NeedsReview row before enabling private messaging.
        if (_features.PrivateEnabled)
        {
            return ConflictProblem("private_review_window_closed");
        }

        var result = await conversations.ApproveLegacyPrivateAsync(
            conversationId,
            GetRequiredActor(),
            request.ClientUserId,
            request.AdminUserId,
            request.ExpectedVersion,
            request.Reason,
            cancellationToken);
        return ToMutationResult(result);
    }

    private ConversationActor GetRequiredActor()
    {
        var userId = User.GetRequiredUserId();
        var isAdmin = User.IsInRole(Roles.Admin);
        var displayName = User.FindFirstValue(ClaimTypes.Name)
            ?? User.Identity?.Name
            ?? $"User {userId}";
        return new ConversationActor(
            userId,
            isAdmin ? null : User.GetRequiredClientId(),
            isAdmin,
            displayName);
    }

    private async Task<bool> IsConversationFeatureEnabledAsync(
        long conversationId,
        ConversationActor actor,
        CancellationToken cancellationToken)
    {
        var access = await conversations.GetAccessAsync(conversationId, actor, cancellationToken);
        return access is not null
            && (access.IsCase
                || (access.IsGroup && _features.GroupEnabled)
                || (access.IsPrivate && _features.PrivateEnabled));
    }

    private ActionResult<ConversationSummary> ToCreateResult(
        ConversationCommandResult<ConversationSummary> result) =>
        result.Status switch
        {
            ConversationCommandStatus.Created => StatusCode(
                StatusCodes.Status201Created,
                result.Value),
            ConversationCommandStatus.Replayed => Ok(result.Value),
            ConversationCommandStatus.Unavailable => NotFound(),
            ConversationCommandStatus.Conflict => ConflictProblem(
                result.ErrorCode ?? "conversation_conflict"),
            _ => ValidationProblem("The conversation request is invalid.")
        };

    private ActionResult<ConversationSummary> ToMutationResult(
        ConversationCommandResult<ConversationSummary> result) =>
        result.Status switch
        {
            ConversationCommandStatus.Success => Ok(result.Value),
            ConversationCommandStatus.Unavailable => NotFound(),
            ConversationCommandStatus.Conflict => ConflictProblem(
                result.ErrorCode ?? "conversation_conflict"),
            _ => ValidationProblem("The conversation command is invalid.")
        };

    private ObjectResult ConflictProblem(string code) =>
        Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conversation conflict",
            detail: "The conversation changed or the request conflicts with existing state.",
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

[ApiController]
[Route("api/v1/admin/clients/{clientId:long}")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class AdminClientConversationsController(
    IConversationService conversations,
    IAuthorizationService authorizationService,
    IOptions<MessagingFeatureOptions> featureOptions) : ControllerBase
{
    private readonly MessagingFeatureOptions _features = featureOptions.Value;

    [HttpPost("group-conversation")]
    [EnableRateLimiting(MessagingRateLimitPolicies.ConversationCreation)]
    public async Task<ActionResult<ConversationSummary>> GetOrCreateGroup(
        long clientId,
        CancellationToken cancellationToken)
    {
        if (!_features.GroupEnabled)
        {
            return NotFound();
        }

        if (!await CanAccessTenantAsync(clientId))
        {
            return NotFound();
        }

        var result = await conversations.GetOrCreateGroupAsync(
            GetRequiredActor(),
            clientId,
            cancellationToken);
        return result.Status switch
        {
            ConversationCommandStatus.Created => StatusCode(201, result.Value),
            ConversationCommandStatus.Replayed => Ok(result.Value),
            ConversationCommandStatus.Unavailable => NotFound(),
            _ => Conflict()
        };
    }

    [HttpGet("conversation-users")]
    public async Task<ActionResult<IReadOnlyList<ConversationDirectoryUser>>> GetUsers(
        long clientId,
        CancellationToken cancellationToken)
    {
        if (!await CanAccessTenantAsync(clientId))
        {
            return NotFound();
        }

        return Ok(await conversations.GetAvailableClientUsersAsync(
            GetRequiredActor(),
            clientId,
            cancellationToken));
    }

    private ConversationActor GetRequiredActor()
    {
        var userId = User.GetRequiredUserId();
        return new ConversationActor(
            userId,
            null,
            IsAdmin: true,
            User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? $"User {userId}");
    }

    private async Task<bool> CanAccessTenantAsync(long clientId) =>
        (await authorizationService.AuthorizeAsync(
            User,
            new TenantResource(clientId),
            TenantAccessRequirement.Instance)).Succeeded;
}

public static class MessagingRateLimitPolicies
{
    public const string MessageSend = "messaging-send";
    public const string ConversationCreation = "messaging-create";
}
