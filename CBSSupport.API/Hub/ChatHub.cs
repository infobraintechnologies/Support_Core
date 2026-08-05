using System.Security.Claims;
using CBSSupport.API.Security;
using CBSSupport.API.Realtime;
using CBSSupport.API.Configuration;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace CBSSupport.API.Hubs;

[Authorize(Policy = Policies.AdminOrClient)]
public sealed class ChatHub(
    IConversationService conversations,
    IOptions<MessagingFeatureOptions> featureOptions,
    ILogger<ChatHub> logger) : Hub<IChatClient>
{
    private readonly MessagingFeatureOptions _features = featureOptions.Value;
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

    public async Task SetTyping(long conversationId, bool isTyping)
    {
        var actor = GetRequiredActor();
        var access = await RequireAccessAsync(conversationId, actor);
        var typing = new TypingChangedEvent(
            conversationId,
            actor.UserId,
            actor.DisplayName,
            isTyping);

        if (access.IsPrivate)
        {
            var recipientUserId = GetPrivateTypingRecipient(access, actor);
            if (recipientUserId is null)
            {
                logger.LogWarning(
                    "Typing recipient was unavailable for user {UserId} in conversation {ConversationId}",
                    actor.UserId,
                    conversationId);
                throw ConversationUnavailable();
            }

            await Clients.User(recipientUserId).TypingChanged(typing);
            return;
        }

        await Clients.GroupExcept(
                RealtimeGroupNames.Conversation(conversationId),
                [Context.ConnectionId])
            .TypingChanged(typing);
    }

    private async Task<ConversationAccess> RequireAccessAsync(
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

        if ((access.IsGroup && !_features.GroupEnabled)
            || (access.IsPrivate && !_features.PrivateEnabled))
        {
            throw ConversationUnavailable();
        }

        return access;
    }

    private static string? GetPrivateTypingRecipient(
        ConversationAccess access,
        ConversationActor actor)
    {
        if (actor.IsAdmin)
        {
            return access.ClientId is > 0 && access.ClientUserId is > 0
                ? RealtimeUserIds.Client(access.ClientId.Value, access.ClientUserId.Value)
                : null;
        }

        return access.AdminUserId is > 0
            ? RealtimeUserIds.Admin(access.AdminUserId.Value)
            : null;
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
