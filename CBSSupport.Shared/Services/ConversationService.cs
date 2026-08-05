using System.Text.Json;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Models;

namespace CBSSupport.Shared.Services;

public sealed class ConversationService(IConversationRepository repository) : IConversationService
{
    private static readonly IReadOnlyDictionary<short, string> TicketSubjects =
        new Dictionary<short, string>
        {
            [ConversationTypes.TrainingTicket] = "Training",
            [ConversationTypes.MigrationTicket] = "Migration",
            [ConversationTypes.SetupTicket] = "Setup",
            [ConversationTypes.CorrectionTicket] = "Correction",
            [ConversationTypes.BugFixTicket] = "Bug Fix",
            [ConversationTypes.NewFeatureTicket] = "New Feature Request",
            [ConversationTypes.FeatureEnhancementTicket] = "Feature Enhancement",
            [ConversationTypes.BackendWorkaroundTicket] = "Backend Workaround"
        };

public Task<ConversationCommandResult<ChatMessage>> CreateCaseAsync(
        ConversationActor actor,
        short instructionTypeId,
        short instructionCategoryId,
        string text,
        string? priority,
        string? remarks,
        DateTime? expiryDate,
        string? ipAddress,
        CancellationToken cancellationToken = default,
        string? subject = null)
    {
        var normalizedText = text?.Trim();
        var isTicket = ConversationTypes.IsTicket(instructionTypeId)
            && instructionCategoryId == InstructionCategories.Ticket;
        var isInquiry = ConversationTypes.IsInquiry(instructionTypeId)
            && instructionCategoryId == InstructionCategories.Inquiry;
        if (actor.IsAdmin
            || actor.UserId <= 0
            || actor.UserId > int.MaxValue
            || actor.ClientId is not > 0
            || (!isTicket && !isInquiry)
            || normalizedText?.Length is not (>= 1 and <= 4000))
        {
            return Task.FromResult(new ConversationCommandResult<ChatMessage>(
                ConversationCommandStatus.Invalid,
                ErrorCode: "invalid_case"));
        }

        var persistedRemarks = remarks;
        if (isTicket)
        {
            var resolvedSubject = string.IsNullOrWhiteSpace(subject)
                ? TicketSubjects.GetValueOrDefault(instructionTypeId, "General Support")
                : subject.Trim();
            persistedRemarks = JsonSerializer.Serialize(new
            {
                priority = priority ?? "Normal",
                userremarks = remarks ?? string.Empty,
                subject = resolvedSubject
            });
        }

        return repository.CreateCaseAsync(
            actor,
            instructionTypeId,
            instructionCategoryId,
            normalizedText,
            persistedRemarks,
            expiryDate,
            ipAddress,
            DateTime.UtcNow,
            cancellationToken);
    }

    public Task<ConversationAccess?> GetAccessAsync(
        long conversationId,
        ConversationActor actor,
        CancellationToken cancellationToken = default)
    {
        if (conversationId <= 0 || actor.UserId <= 0 || !HasValidClientIdentity(actor))
        {
            return Task.FromResult<ConversationAccess?>(null);
        }

        if (actor.IsAdmin)
        {
            return repository.GetForAdminAsync(conversationId, actor.UserId, cancellationToken);
        }

        return actor.ClientId is > 0
            ? repository.GetForClientAsync(
                conversationId,
                actor.ClientId.Value,
                checked((int)actor.UserId),
                cancellationToken)
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
                ClientMessageId: null,
                Sequence: 0,
                Attachments: []);
    }

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(
        ConversationActor actor,
        int limit = 50,
        long? beforeConversationId = null,
        CancellationToken cancellationToken = default) =>
        HasValidClientIdentity(actor)
            ? repository.ListAsync(
                actor,
                Math.Clamp(limit, 1, 100),
                beforeConversationId,
                cancellationToken)
            : Task.FromResult<IReadOnlyList<ConversationSummary>>([]);

    public Task<ConversationCommandResult<ConversationSummary>> GetOrCreateGroupAsync(
        ConversationActor actor,
        long? adminSelectedClientId,
        CancellationToken cancellationToken = default)
    {
        var clientId = actor.IsAdmin ? adminSelectedClientId : actor.ClientId;
        return clientId is > 0 && HasValidClientIdentity(actor)
            ? repository.GetOrCreateGroupAsync(actor, clientId.Value, cancellationToken)
            : Task.FromResult(new ConversationCommandResult<ConversationSummary>(
                ConversationCommandStatus.Invalid,
                ErrorCode: "invalid_client"));
    }

    public Task<ConversationCommandResult<ConversationSummary>> GetOrCreatePrivateAsync(
        ConversationActor actor,
        long counterpartyUserId,
        CancellationToken cancellationToken = default) =>
        counterpartyUserId > 0 && HasValidClientIdentity(actor)
            ? repository.GetOrCreatePrivateAsync(actor, counterpartyUserId, cancellationToken)
            : Task.FromResult(new ConversationCommandResult<ConversationSummary>(
                ConversationCommandStatus.Invalid,
                ErrorCode: "invalid_counterparty"));

    public Task<ConversationPage<ConversationMessage>?> GetMessagesAsync(
        long conversationId,
        ConversationActor actor,
        int limit,
        long? beforeSequence,
        long? afterSequence,
        CancellationToken cancellationToken = default)
    {
        if (conversationId <= 0
            || !HasValidClientIdentity(actor)
            || (beforeSequence.HasValue && afterSequence.HasValue)
            || beforeSequence is < 1
            || afterSequence is < 0)
        {
            return Task.FromResult<ConversationPage<ConversationMessage>?>(null);
        }

        return repository.GetMessagesAsync(
            conversationId,
            actor,
            Math.Clamp(limit, 1, 100),
            beforeSequence,
            afterSequence,
            cancellationToken);
    }

    public Task<ConversationCommandResult<ConversationMessage>> SendMessageAsync(
        long conversationId,
        ConversationActor actor,
        Guid clientMessageId,
        string? text,
        IReadOnlyList<Guid> attachmentIds,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var normalized = text?.Trim();
        attachmentIds ??= [];
        if (conversationId <= 0
            || !HasValidClientIdentity(actor)
            || clientMessageId == Guid.Empty
            || (string.IsNullOrWhiteSpace(normalized) && attachmentIds.Count == 0)
            || normalized?.Length > 4000
            || attachmentIds.Count > 5
            || attachmentIds.Any(id => id == Guid.Empty)
            || attachmentIds.Distinct().Count() != attachmentIds.Count)
        {
            return Task.FromResult(new ConversationCommandResult<ConversationMessage>(
                ConversationCommandStatus.Invalid,
                ErrorCode: "invalid_message"));
        }

        return repository.SendMessageAsync(
            conversationId,
            actor,
            clientMessageId,
            normalized,
            attachmentIds,
            ipAddress,
            cancellationToken);
    }

    public Task<ConversationCommandResult<long>> AdvanceReadCursorAsync(
        long conversationId,
        ConversationActor actor,
        long throughSequence,
        CancellationToken cancellationToken = default) =>
        conversationId > 0 && throughSequence >= 0 && HasValidClientIdentity(actor)
            ? repository.AdvanceReadCursorAsync(
                conversationId,
                actor,
                throughSequence,
                cancellationToken)
            : Task.FromResult(new ConversationCommandResult<long>(
                ConversationCommandStatus.Invalid,
                ErrorCode: "invalid_read_cursor"));

    public Task<ConversationCommandResult<ConversationSummary>> TransferAsync(
        long conversationId,
        ConversationActor actor,
        long targetAdminUserId,
        long expectedVersion,
        string? reason,
        CancellationToken cancellationToken = default) =>
        actor.IsAdmin && conversationId > 0 && targetAdminUserId > 0 && expectedVersion > 0
            ? repository.TransferAsync(
                conversationId,
                actor,
                targetAdminUserId,
                expectedVersion,
                reason?.Trim(),
                cancellationToken)
            : Task.FromResult(new ConversationCommandResult<ConversationSummary>(
                ConversationCommandStatus.Invalid,
                ErrorCode: "invalid_transfer"));

    public Task<ConversationCommandResult<ConversationSummary>> ArchiveAsync(
        long conversationId,
        ConversationActor actor,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        conversationId > 0 && expectedVersion > 0 && HasValidClientIdentity(actor)
            ? repository.ArchiveAsync(
                conversationId,
                actor,
                expectedVersion,
                cancellationToken)
            : Task.FromResult(new ConversationCommandResult<ConversationSummary>(
                ConversationCommandStatus.Invalid,
                ErrorCode: "invalid_archive"));

    public Task<ConversationCommandResult<ConversationSummary>> ApproveLegacyPrivateAsync(
        long conversationId,
        ConversationActor actor,
        int clientUserId,
        long adminUserId,
        long expectedVersion,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var normalizedReason = reason?.Trim();
        return actor.IsAdmin
               && conversationId > 0
               && clientUserId > 0
               && adminUserId > 0
               && expectedVersion > 0
               && !string.IsNullOrWhiteSpace(normalizedReason)
            ? repository.ApproveLegacyPrivateAsync(
                conversationId,
                actor,
                clientUserId,
                adminUserId,
                expectedVersion,
                normalizedReason,
                cancellationToken)
            : Task.FromResult(new ConversationCommandResult<ConversationSummary>(
                ConversationCommandStatus.Invalid,
                ErrorCode: "invalid_private_review"));
    }

    public Task<IReadOnlyList<ConversationDirectoryUser>> GetAvailableAdminsAsync(
        ConversationActor actor,
        CancellationToken cancellationToken = default) =>
        repository.GetAvailableAdminsAsync(cancellationToken);

    public Task<IReadOnlyList<ConversationDirectoryUser>> GetAvailableClientUsersAsync(
        ConversationActor actor,
        long clientId,
        CancellationToken cancellationToken = default) =>
        actor.IsAdmin && clientId > 0
            ? repository.GetAvailableClientUsersAsync(clientId, cancellationToken)
            : Task.FromResult<IReadOnlyList<ConversationDirectoryUser>>([]);

    private static bool HasValidClientIdentity(ConversationActor actor) =>
        actor.IsAdmin || actor.UserId is > 0 and <= int.MaxValue;
}
