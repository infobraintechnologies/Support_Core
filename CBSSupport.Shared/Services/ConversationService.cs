using CBSSupport.Shared.Contracts;

namespace CBSSupport.Shared.Services;

public sealed class ConversationService(IConversationRepository repository) : IConversationService
{
    public Task<ConversationAccess?> GetAccessAsync(
        long conversationId,
        ConversationActor actor,
        CancellationToken cancellationToken = default)
    {
        if (conversationId <= 0 || actor.UserId <= 0)
        {
            return Task.FromResult<ConversationAccess?>(null);
        }

        if (actor.IsAdmin)
        {
            return repository.GetForAdminAsync(conversationId, cancellationToken);
        }

        return actor.ClientId is > 0
            ? repository.GetForClientAsync(conversationId, actor.ClientId.Value, cancellationToken)
            : Task.FromResult<ConversationAccess?>(null);
    }

    public async Task<ConversationMessage?> CreateMessageAsync(
        long conversationId,
        ConversationActor actor,
        string text,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var normalizedText = text.Trim();
        if (normalizedText.Length is < 1 or > 4000)
        {
            return null;
        }

        var access = await GetAccessAsync(conversationId, actor, cancellationToken);
        if (access is null)
        {
            return null;
        }

        var userId = checked((int)actor.UserId);
        var sentAt = DateTime.UtcNow;
        var messageId = actor.IsAdmin
            ? await repository.InsertMessageForAdminAsync(
                conversationId,
                userId,
                normalizedText,
                sentAt,
                ipAddress,
                cancellationToken)
            : await repository.InsertMessageForClientAsync(
                conversationId,
                actor.ClientId!.Value,
                userId,
                normalizedText,
                sentAt,
                ipAddress,
                cancellationToken);

        return messageId is null
            ? null
            : new ConversationMessage(
                messageId.Value,
                conversationId,
                normalizedText,
                sentAt,
                new ConversationSender(
                    actor.UserId,
                    actor.DisplayName,
                    actor.IsAdmin ? "Admin" : "Client"),
                AttachmentId: null);
    }
}
