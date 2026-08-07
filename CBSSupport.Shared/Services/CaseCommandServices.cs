using System.Diagnostics;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Data;
using Dapper;
using Npgsql;

namespace CBSSupport.Shared.Services;

/// <summary>
/// A status command whose actor has already been derived from an authenticated Admin principal.
/// </summary>
public sealed record CaseStatusUpdateCommand(
    long CaseId,
    bool IsCompleted,
    long ActorUserId,
    long ExpectedVersion);

/// <summary>
/// A ticket detail command whose actor has already been derived from an authenticated Admin principal.
/// </summary>
public sealed record TicketUpdateCommand(
    long TicketId,
    string Instruction,
    string? Remarks,
    DateTime? ExpiryDate,
    long ActorUserId,
    long ExpectedVersion);

public interface ITicketService
{
    Task<CaseMutationResult> UpdateStatusAsync(
        CaseStatusUpdateCommand command,
        CancellationToken cancellationToken = default);

    Task<CaseMutationResult> UpdateAsync(
        TicketUpdateCommand command,
        CancellationToken cancellationToken = default);
}

public interface IInquiryService
{
    Task<CaseMutationResult> UpdateStatusAsync(
        CaseStatusUpdateCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the transaction for a case mutation: root state, access version, audits,
/// recipient notifications, and outbox record commit or roll back together.
/// </summary>
public interface ICaseMutationCommandHandler
{
    Task<CaseMutationResult> ExecuteAsync(
        CaseMutationCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class TicketService(ICaseMutationCommandHandler commands) : ITicketService
{
    public Task<CaseMutationResult> UpdateStatusAsync(
        CaseStatusUpdateCommand command,
        CancellationToken cancellationToken = default) =>
        commands.ExecuteAsync(new CaseMutationCommand(
            command.CaseId,
            InstructionCategories.Ticket,
            ConversationKinds.Ticket,
            command.ActorUserId,
            command.ExpectedVersion,
            command.IsCompleted ? "TicketResolved" : "TicketReopened",
            "TicketStatusUpdated",
            null,
            null,
            null,
            command.IsCompleted), cancellationToken);

    public Task<CaseMutationResult> UpdateAsync(
        TicketUpdateCommand command,
        CancellationToken cancellationToken = default) =>
        commands.ExecuteAsync(new CaseMutationCommand(
            command.TicketId,
            InstructionCategories.Ticket,
            ConversationKinds.Ticket,
            command.ActorUserId,
            command.ExpectedVersion,
            "TicketUpdated",
            "TicketUpdated",
            command.Instruction,
            command.Remarks,
            command.ExpiryDate,
            IsCompleted: false), cancellationToken);
}

public sealed class InquiryService(ICaseMutationCommandHandler commands) : IInquiryService
{
    public Task<CaseMutationResult> UpdateStatusAsync(
        CaseStatusUpdateCommand command,
        CancellationToken cancellationToken = default) =>
        commands.ExecuteAsync(new CaseMutationCommand(
            command.CaseId,
            InstructionCategories.Inquiry,
            ConversationKinds.Inquiry,
            command.ActorUserId,
            command.ExpectedVersion,
            command.IsCompleted ? "InquiryCompleted" : "InquiryReopened",
            "InquiryStatusUpdated",
            null,
            null,
            null,
            command.IsCompleted), cancellationToken);
}

public sealed record CaseMutationCommand(
    long CaseId,
    short CategoryId,
    string ConversationKind,
    long ActorUserId,
    long ExpectedVersion,
    string EventType,
    string AuditAction,
    string? Instruction,
    string? Remarks,
    DateTime? ExpiryDate,
    bool IsCompleted);

public sealed class CaseMutationCommandHandler(
    string connectionString,
    ISecurityAuditWriter? securityAudit = null) : ICaseMutationCommandHandler
{
    private readonly ISecurityAuditWriter _securityAudit = securityAudit ?? new NullSecurityAuditWriter();

    public async Task<CaseMutationResult> ExecuteAsync(
        CaseMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.CaseId <= 0 || command.ExpectedVersion <= 0 || command.ActorUserId <= 0 || command.ActorUserId > int.MaxValue)
        {
            return new(CaseMutationStatus.NotFound);
        }

        var actorUserId = checked((int)command.ActorUserId);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var occurredAt = DateTime.UtcNow;
            const string updateSql = """
                WITH changed_access AS (
                    UPDATE digital.conversation_access access
                    SET version = access.version + 1
                    FROM digital.instructions root
                    WHERE access.conversation_id = root.id
                      AND access.conversation_id = @CaseId
                      AND access.client_id = root.client_id
                      AND access.conversation_kind = @ConversationKind
                      AND access.state = 'Active'
                      AND access.version = @ExpectedVersion
                      AND root.inst_category_id = @CategoryId
                      AND root.instruction_id = root.id
                      AND (NOT @IsEdit OR COALESCE(root.completed, FALSE) = FALSE)
                      AND (@IsEdit OR COALESCE(root.completed, FALSE) IS DISTINCT FROM @IsCompleted)
                    RETURNING access.client_id AS ClientId, access.version AS Version
                ), changed_instruction AS (
                    UPDATE digital.instructions root
                    SET completed = CASE WHEN @IsEdit THEN root.completed ELSE @IsCompleted END,
                        completed_by = CASE WHEN @IsEdit THEN root.completed_by ELSE @ActorUserId END,
                        completed_on = CASE WHEN @IsEdit THEN root.completed_on WHEN @IsCompleted THEN @OccurredAt ELSE NULL END,
                        instruction = CASE WHEN @IsEdit THEN @Instruction ELSE root.instruction END,
                        remarks = CASE WHEN @IsEdit THEN @Remarks ELSE root.remarks END,
                        expiry_date = CASE WHEN @IsEdit THEN @ExpiryDate ELSE root.expiry_date END,
                        edit_date = @OccurredAt,
                        edit_user = @ActorUserId
                    FROM changed_access access
                    WHERE root.id = @CaseId AND root.client_id = access.ClientId
                    RETURNING root.id
                )
                SELECT ClientId, Version FROM changed_access
                WHERE EXISTS (SELECT 1 FROM changed_instruction);
                """;
            var isEdit = command.Instruction is not null;
            var changed = await connection.QuerySingleOrDefaultAsync<CaseMutationRow>(new CommandDefinition(
                updateSql,
                new
                {
                    command.CaseId,
                    command.CategoryId,
                    command.ConversationKind,
                    command.ExpectedVersion,
                    command.IsCompleted,
                    IsEdit = isEdit,
                    command.Instruction,
                    command.Remarks,
                    command.ExpiryDate,
                    ActorUserId = actorUserId,
                    OccurredAt = occurredAt
                },
                transaction,
                cancellationToken: cancellationToken));
            if (changed is null)
            {
                const string stateSql = """
                    SELECT access.version
                    FROM digital.conversation_access access
                    JOIN digital.instructions root ON root.id = access.conversation_id
                    WHERE access.conversation_id = @CaseId
                      AND access.conversation_kind = @ConversationKind
                      AND root.inst_category_id = @CategoryId
                      AND root.instruction_id = root.id;
                    """;
                var currentVersion = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                    stateSql,
                    new { command.CaseId, command.ConversationKind, command.CategoryId },
                    transaction,
                    cancellationToken: cancellationToken));
                await transaction.RollbackAsync(CancellationToken.None);
                return currentVersion is null ? new(CaseMutationStatus.NotFound)
                    : currentVersion != command.ExpectedVersion ? new(CaseMutationStatus.Conflict)
                    : new(CaseMutationStatus.InvalidState, currentVersion);
            }

            var eventId = Guid.NewGuid();
            var changedFieldNames = isEdit
                ? "[\"instruction\",\"remarks\",\"expiryDate\"]"
                : "[\"completed\",\"completedBy\",\"completedOn\"]";
            const string auditAndOutboxSql = """
                INSERT INTO digital.case_audit (
                    case_id, case_type, client_id, actor_user_id, actor_type,
                    action, previous_version, resulting_version, occurred_at,
                    changed_fields, correlation_id, is_system_generated)
                VALUES (
                    @CaseId, @ConversationKind, @ClientId, @ActorUserId, 'Admin',
                    @AuditAction, @PreviousVersion, @Version, @OccurredAt,
                    jsonb_build_object('operation', @Operation, 'fields', CAST(@ChangedFieldNames AS jsonb)),
                    @CorrelationId, FALSE);

                INSERT INTO digital.conversation_audit (conversation_id, client_id, action, actor_kind,
                    admin_user_id, client_user_id, occurred_at, details)
                VALUES (@CaseId, @ClientId, @AuditAction, 'Admin', @ActorUserId, NULL, @OccurredAt,
                    jsonb_build_object('caseVersion', @Version));
                INSERT INTO digital.conversation_outbox (event_id, conversation_id, client_id, conversation_kind,
                    conversation_state, client_user_id, admin_user_id, access_version, message_id, event_type,
                    schema_version, payload, occurred_at, available_at, attempt_count, idempotency_key)
                VALUES (@EventId, @CaseId, @ClientId, @ConversationKind, 'Active', NULL, NULL, @Version,
                    NULL, @EventType, 1,
                    jsonb_build_object('eventId', @EventId, 'conversationId', @CaseId, 'caseVersion', @Version),
                    @OccurredAt, @OccurredAt, 0, @OutboxIdempotencyKey);
                """;
            await connection.ExecuteAsync(new CommandDefinition(
                auditAndOutboxSql,
                new
                {
                    command.CaseId,
                    changed.ClientId,
                    changed.Version,
                    command.AuditAction,
                    ActorUserId = actorUserId,
                    OccurredAt = occurredAt,
                    EventId = eventId,
                    command.ConversationKind,
                    command.EventType,
                    PreviousVersion = command.ExpectedVersion,
                    Operation = isEdit ? "DetailsUpdated" : "StatusTransition",
                    ChangedFieldNames = changedFieldNames,
                    CorrelationId = Activity.Current?.Id,
                    OutboxIdempotencyKey = $"case:{command.CaseId}:mutation:{changed.Version}"
                },
                transaction,
                cancellationToken: cancellationToken));
            await _securityAudit.AppendAsync(
                connection,
                transaction,
                new SecurityAuditEvent(
                    changed.ClientId,
                    SecurityAuditActorKinds.Admin,
                    actorUserId,
                    command.ConversationKind,
                    command.CaseId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    command.AuditAction,
                    SecurityAuditOutcomes.Success,
                    new DateTimeOffset(occurredAt, TimeSpan.Zero),
                    Activity.Current?.Id,
                    null,
                    new Dictionary<string, string?> { ["feature"] = "case" },
                    new Dictionary<string, string?>
                    {
                        ["operation"] = isEdit ? "DetailsUpdated" : "StatusTransition",
                        ["version"] = changed.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }),
                cancellationToken);
            await CaseNotificationWriter.InsertAsync(
                connection,
                transaction,
                command.CaseId,
                changed.ClientId,
                command.EventType,
                changed.Version,
                eventId,
                $"case:{command.CaseId}:mutation:{changed.Version}",
                actorIsAdmin: true,
                actorUserId,
                occurredAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(CaseMutationStatus.Updated, changed.Version, changed.ClientId);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private sealed record CaseMutationRow(long ClientId, long Version);
}
