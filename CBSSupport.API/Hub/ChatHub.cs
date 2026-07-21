using System.Security.Claims;
using CBSSupport.API.Security;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CBSSupport.API.Hubs;

[Authorize(Policy = Policies.AdminOrClient)]
public sealed class ChatHub(
    IConversationService conversations,
    ILogger<ChatHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var actor = GetRequiredActor();
        var audienceGroup = actor.IsAdmin
            ? RealtimeGroupNames.Admins
            : RealtimeGroupNames.Tenant(actor.ClientId!.Value);

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            audienceGroup,
            Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public async Task JoinConversation(long conversationId)
    {
        var actor = GetRequiredActor();
        await RequireAccessAsync(conversationId, actor);

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Conversation(conversationId),
            Context.ConnectionAborted);

        logger.LogInformation(
            "User {UserId} joined conversation {ConversationId}",
            actor.UserId,
            conversationId);
    }

    public async Task LeaveConversation(long conversationId)
    {
        var actor = GetRequiredActor();
        await RequireAccessAsync(conversationId, actor);

        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Conversation(conversationId),
            Context.ConnectionAborted);
    }

    public async Task<ConversationMessage> SendMessage(
        long conversationId,
        SendConversationMessageRequest request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Text)
            || request.Text.Trim().Length > 4000)
        {
            throw new HubException("Message text must be between 1 and 4000 characters.");
        }

        if (request.AttachmentIds is { Count: > 0 })
        {
            throw new HubException("Attachments are not supported by this chat command.");
        }

        var actor = GetRequiredActor();
        await RequireAccessAsync(conversationId, actor);

        var message = await conversations.CreateMessageAsync(
            conversationId,
            actor,
            request.Text,
            Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString(),
            Context.ConnectionAborted);
        if (message is null)
        {
            throw ConversationUnavailable();
        }

        await Clients.GroupExcept(
                RealtimeGroupNames.Conversation(conversationId),
                Context.ConnectionId)
            .SendAsync("MessageCreated", message, Context.ConnectionAborted);

        logger.LogInformation(
            "User {UserId} sent message {MessageId} to conversation {ConversationId}",
            actor.UserId,
            message.Id,
            conversationId);

        return message;
    }

    public async Task SetTyping(long conversationId, bool isTyping)
    {
        var actor = GetRequiredActor();
        await RequireAccessAsync(conversationId, actor);

        await Clients.GroupExcept(
                RealtimeGroupNames.Conversation(conversationId),
                Context.ConnectionId)
            .SendAsync(
                "TypingChanged",
                new
                {
                    ConversationId = conversationId,
                    actor.UserId,
                    actor.DisplayName,
                    IsTyping = isTyping
                },
                Context.ConnectionAborted);
    }

    private async Task RequireAccessAsync(
        long conversationId,
        ConversationActor actor)
    {
        if (conversationId <= 0)
        {
            throw ConversationUnavailable();
        }

        var access = await conversations.GetAccessAsync(
            conversationId,
            actor,
            Context.ConnectionAborted);
        if (access is null)
        {
            logger.LogWarning(
                "User {UserId} was denied access to conversation {ConversationId}",
                actor.UserId,
                conversationId);
            throw ConversationUnavailable();
        }
    }

    private ConversationActor GetRequiredActor()
    {
        var principal = Context.User;
        if (principal is null || !principal.TryGetUserId(out var userId))
        {
            throw new HubException("Authenticated user identity is unavailable.");
        }

        var isAdmin = principal.IsInRole(Roles.Admin);
        var isClient = principal.IsInRole(Roles.Client);
        if (isAdmin == isClient)
        {
            throw new HubException("Authenticated user role is unavailable.");
        }

        long? clientId = null;
        if (isClient)
        {
            if (!principal.TryGetClientId(out var requiredClientId))
            {
                throw new HubException("Authenticated tenant identity is unavailable.");
            }

            clientId = requiredClientId;
        }

        var displayName = principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.Identity?.Name
            ?? $"User {userId}";

        return new ConversationActor(userId, clientId, isAdmin, displayName);
    }

    private static HubException ConversationUnavailable() =>
        new("Conversation unavailable.");
}
