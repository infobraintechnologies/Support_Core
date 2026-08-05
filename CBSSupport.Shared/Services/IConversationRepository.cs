using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Models;

namespace CBSSupport.Shared.Services;

public interface IConversationRepository
{
    Task<ConversationCommandResult<ChatMessage>> CreateCaseAsync(
        ConversationActor actor,
        short instructionTypeId,
        short instructionCategoryId,
        string text,
        string? persistedRemarks,
        DateTime? expiryDate,
        string? ipAddress,
        DateTime occurredAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ConversationAccess?> GetForAdminAsync(
        long conversationId,
        long adminUserId,
        CancellationToken cancellationToken = default);

    Task<ConversationAccess?> GetForAdminAsync(
        long conversationId,
        CancellationToken cancellationToken = default) =>
        GetForAdminAsync(conversationId, long.MaxValue, cancellationToken);

    Task<ConversationAccess?> GetForClientAsync(
        long conversationId,
        long clientId,
        CancellationToken cancellationToken = default);

    Task<ConversationAccess?> GetForClientAsync(
        long conversationId,
        long clientId,
        int clientUserId,
        CancellationToken cancellationToken = default) =>
        GetForClientAsync(conversationId, clientId, cancellationToken);

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

    Task<IReadOnlyList<ConversationSummary>> ListAsync(
        ConversationActor actor,
        int limit,
        long? beforeConversationId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ConversationCommandResult<ConversationSummary>> GetOrCreateGroupAsync(
        ConversationActor actor,
        long clientId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ConversationCommandResult<ConversationSummary>> GetOrCreatePrivateAsync(
        ConversationActor actor,
        long counterpartyUserId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ConversationPage<ConversationMessage>?> GetMessagesAsync(
        long conversationId,
        ConversationActor actor,
        int limit,
        long? beforeSequence,
        long? afterSequence,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ConversationCommandResult<ConversationMessage>> SendMessageAsync(
        long conversationId,
        ConversationActor actor,
        Guid clientMessageId,
        string? text,
        IReadOnlyList<Guid> attachmentIds,
        string? ipAddress,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ConversationCommandResult<long>> AdvanceReadCursorAsync(
        long conversationId,
        ConversationActor actor,
        long throughSequence,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ConversationCommandResult<ConversationSummary>> TransferAsync(
        long conversationId,
        ConversationActor actor,
        long targetAdminUserId,
        long expectedVersion,
        string? reason,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ConversationCommandResult<ConversationSummary>> ArchiveAsync(
        long conversationId,
        ConversationActor actor,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ConversationCommandResult<ConversationSummary>> ApproveLegacyPrivateAsync(
        long conversationId,
        ConversationActor actor,
        int clientUserId,
        long adminUserId,
        long expectedVersion,
        string reason,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<ConversationDirectoryUser>> GetAvailableAdminsAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<ConversationDirectoryUser>> GetAvailableClientUsersAsync(
        long clientId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
