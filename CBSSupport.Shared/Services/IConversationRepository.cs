using CBSSupport.Shared.Contracts;

namespace CBSSupport.Shared.Services;

public interface IConversationRepository
{
    Task<ConversationAccess?> GetForAdminAsync(
        long conversationId,
        CancellationToken cancellationToken = default);

    Task<ConversationAccess?> GetForClientAsync(
        long conversationId,
        long clientId,
        CancellationToken cancellationToken = default);

    Task<long?> InsertMessageForAdminAsync(
        long conversationId,
        int userId,
        string text,
        DateTime sentAt,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task<long?> InsertMessageForClientAsync(
        long conversationId,
        long clientId,
        int userId,
        string text,
        DateTime sentAt,
        string? ipAddress,
        CancellationToken cancellationToken = default);
}
