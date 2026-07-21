using CBSSupport.Shared.Contracts;

namespace CBSSupport.Shared.Services;

public interface IConversationService
{
    Task<ConversationAccess?> GetAccessAsync(
        long conversationId,
        ConversationActor actor,
        CancellationToken cancellationToken = default);

    Task<ConversationMessage?> CreateMessageAsync(
        long conversationId,
        ConversationActor actor,
        string text,
        string? ipAddress,
        CancellationToken cancellationToken = default);
}
