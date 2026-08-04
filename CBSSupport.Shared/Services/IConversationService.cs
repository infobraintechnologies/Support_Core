using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Models;

namespace CBSSupport.Shared.Services;

public interface IConversationService
{
    Task<ConversationCommandResult<ChatMessage>> CreateCaseAsync(
        ConversationActor actor,
        short instructionTypeId,
        short instructionCategoryId,
        string text,
        string? priority,
        string? remarks,
        DateTime? expiryDate,
        string? ipAddress,
        CancellationToken cancellationToken = default,
        string? subject = null) =>
        throw new NotSupportedException();

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

    Task<IReadOnlyList<ConversationSummary>> ListAsync(
        ConversationActor actor,
        int limit = 50,
        long? beforeConversationId = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ConversationCommandResult<ConversationSummary>> GetOrCreateGroupAsync(
        ConversationActor actor,
        long? adminSelectedClientId,
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
        ConversationActor actor,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<ConversationDirectoryUser>> GetAvailableClientUsersAsync(
        ConversationActor actor,
        long clientId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
