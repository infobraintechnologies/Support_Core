using CBSSupport.Shared.Models;
using CBSSupport.Shared.ViewModels;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CBSSupport.Shared.Services;

/// <summary>
/// Reads the legacy instruction-backed conversation projections still consumed by
/// the compatibility instruction routes. Durable conversation commands remain in
/// <see cref="IConversationService"/>.
/// </summary>
public interface IConversationQueryService
{
    Task<SidebarViewModel> GetSidebarAsync(
        long clientId,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<ChatMessage>> GetInstructionTicketsForUserAsync(
        long clientId,
        int clientAuthUserId,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<ChatMessage>> GetConversationsByInstructionTypeAsync(
        short instructionTypeId,
        long? clientId = null,
        CancellationToken cancellationToken = default);

    Task<ChatMessage?> GetInstructionByIdAsync(
        long instructionId,
        long? clientId = null,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<ChatMessage>> GetMessagesAsync(
        long conversationId,
        long? clientId = null,
        CancellationToken cancellationToken = default);
}

public sealed class ConversationQueryService(
    string connectionString,
    ILogger<ConversationQueryService> logger) : IConversationQueryService
{
    public async Task<SidebarViewModel> GetSidebarAsync(
        long clientId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string groupChatSql = """
            SELECT DISTINCT ON (i.instruction_id)
                i.instruction_id AS ConversationId,
                'Company Group Chat' AS DisplayName,
                COALESCE(i.instruction, 'Start the conversation') AS Subtitle,
                'G' AS AvatarInitials,
                'admin-avatar-bg-success' AS AvatarClass,
                'support-group' AS Route
            FROM digital.instructions i
            WHERE i.client_id = @ClientId
              AND i.inst_type_id = 100
              AND i.inst_category_id = 100
              AND i.instruction_id IS NOT NULL
            ORDER BY i.instruction_id, i.datetime DESC
            LIMIT 1;
            """;
        const string privateChatSql = """
            SELECT DISTINCT ON (i.instruction_id)
                i.instruction_id AS ConversationId,
                COALESCE(u.full_name, 'Client User') AS DisplayName,
                i.instruction AS Subtitle,
                'P' AS AvatarInitials, 'admin-avatar-bg-purple' AS AvatarClass,
                'support-private' AS Route
            FROM digital.instructions i
            LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
            WHERE i.client_id = @ClientId
              AND i.inst_type_id = 101
              AND i.instruction_id IS NOT NULL
            ORDER BY i.instruction_id, i.datetime DESC;
            """;
        const string internalChatSql = """
            SELECT DISTINCT ON (i.instruction_id)
                i.instruction_id AS ConversationId,
                'Internal Discussion' AS DisplayName,
                i.instruction AS Subtitle,
                'I' AS AvatarInitials, 'avatar-bg-green' AS AvatarClass,
                'internal-team-chat' AS Route
            FROM digital.instructions i
            WHERE i.client_id = @ClientId AND i.inst_type_id = 105 AND i.instruction_id IS NOT NULL
            ORDER BY i.instruction_id, i.datetime DESC;
            """;
        const string ticketSql = """
            SELECT DISTINCT ON (i.instruction_id)
                i.instruction_id AS ConversationId,
                t.inst_type_name AS DisplayName,
                i.instruction AS Subtitle,
                'T' AS AvatarInitials, 'avatar-bg-orange' AS AvatarClass,
                CASE i.inst_type_id
                    WHEN 110 THEN 'ticket/training' WHEN 111 THEN 'ticket/migration'
                    WHEN 112 THEN 'ticket/setup' WHEN 113 THEN 'ticket/correction'
                    WHEN 114 THEN 'ticket/bug-fix' WHEN 115 THEN 'ticket/new-feature'
                    WHEN 116 THEN 'ticket/feature-enhancement' WHEN 117 THEN 'ticket/backend-workaround'
                    ELSE ''
                END AS Route
            FROM digital.instructions i JOIN digital.inst_types t ON i.inst_type_id = t.id
            WHERE i.client_id = @ClientId AND i.inst_type_id = ANY(@TicketTypeIds) AND i.instruction_id IS NOT NULL
            ORDER BY i.instruction_id, i.datetime DESC;
            """;
        const string inquirySql = """
            SELECT DISTINCT ON (i.instruction_id)
                i.instruction_id AS ConversationId,
                t.inst_type_name AS DisplayName,
                i.instruction AS Subtitle,
                'Q' AS AvatarInitials, 'avatar-bg-cyan' AS AvatarClass,
                CASE i.inst_type_id
                    WHEN 121 THEN 'inquiry/accounts' WHEN 122 THEN 'inquiry/sales'
                    ELSE ''
                END AS Route
            FROM digital.instructions i JOIN digital.inst_types t ON i.inst_type_id = t.id
            WHERE i.client_id = @ClientId AND i.inst_type_id = ANY(@InquiryTypeIds) AND i.instruction_id IS NOT NULL
            ORDER BY i.instruction_id, i.datetime DESC;
            """;

        var sidebar = new SidebarViewModel();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var parameters = new { ClientId = clientId };
        var groupChats = (await connection.QueryAsync<SidebarChatItem>(new CommandDefinition(
            groupChatSql, parameters, cancellationToken: cancellationToken))).ToList();
        foreach (var groupChat in groupChats)
        {
            sidebar.GroupChats.Add(new SidebarChatItem
            {
                ConversationId = groupChat.ConversationId.ToString(),
                DisplayName = groupChat.DisplayName,
                Subtitle = groupChat.Subtitle,
                AvatarInitials = "G",
                AvatarClass = "admin-avatar-bg-success",
                Route = "support-group"
            });
        }

        if (groupChats.Count == 0)
        {
            sidebar.GroupChats.Add(new SidebarChatItem
            {
                ConversationId = "0",
                DisplayName = "Company Group Chat",
                Subtitle = "Click to start group conversation",
                AvatarInitials = "G",
                AvatarClass = "admin-avatar-bg-success",
                Route = "support-group"
            });
        }

        sidebar.PrivateChats.AddRange(await connection.QueryAsync<SidebarChatItem>(new CommandDefinition(
            privateChatSql, parameters, cancellationToken: cancellationToken)));
        sidebar.InternalChats.AddRange(await connection.QueryAsync<SidebarChatItem>(new CommandDefinition(
            internalChatSql, parameters, cancellationToken: cancellationToken)));

        var ticketChats = (await connection.QueryAsync<SidebarChatItem>(new CommandDefinition(
            ticketSql,
            new { ClientId = clientId, TicketTypeIds = Enumerable.Range(110, 8).ToArray() },
            cancellationToken: cancellationToken))).ToList();
        foreach (var ticket in ticketChats)
        {
            ticket.DisplayName = $"#{ticket.ConversationId} - {ticket.DisplayName}";
        }
        sidebar.TicketChats.AddRange(ticketChats);
        sidebar.InquiryChats.AddRange(await connection.QueryAsync<SidebarChatItem>(new CommandDefinition(
            inquirySql,
            new { ClientId = clientId, InquiryTypeIds = new[] { 121, 122 } },
            cancellationToken: cancellationToken)));

        logger.LogDebug("Loaded legacy conversation sidebar for client {ClientId}", clientId);
        return sidebar;
    }

    public async Task<IEnumerable<ChatMessage>> GetInstructionTicketsForUserAsync(
        long clientId,
        int clientAuthUserId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string sql = """
            SELECT DISTINCT ON (instruction_id)
                id, instruction_id, datetime, instruction, status, inst_type_id
            FROM digital.instructions
            WHERE client_id = @ClientId
              AND client_auth_user_id = @ClientAuthUserId
              AND instruction_id IS NOT NULL
            ORDER BY instruction_id, datetime DESC;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.QueryAsync<ChatMessage>(new CommandDefinition(
            sql, new { ClientId = clientId, ClientAuthUserId = clientAuthUserId }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<ChatMessage>> GetConversationsByInstructionTypeAsync(
        short instructionTypeId,
        long? clientId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string sql = """
            SELECT i.id, i.datetime, i.instruction, i.assigned_to, i.status, i.completed, i.instruction_id,
                   ca.version AS Version
            FROM digital.instructions i
            LEFT JOIN digital.conversation_access ca ON ca.conversation_id = i.id
            WHERE i.inst_type_id = @InstructionTypeId
              AND (@ClientId IS NULL OR i.client_id = @ClientId)
            ORDER BY i.datetime DESC;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.QueryAsync<ChatMessage>(new CommandDefinition(
            sql, new { InstructionTypeId = instructionTypeId, ClientId = clientId }, cancellationToken: cancellationToken));
    }

    public async Task<ChatMessage?> GetInstructionByIdAsync(
        long instructionId,
        long? clientId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string sql = """
            SELECT id, datetime, inst_category_id, inst_type_id, instruction_id,
                   instruction, assigned_to, status, completed, attachment_id, client_id
            FROM digital.instructions
            WHERE id = @InstructionId
              AND (@ClientId IS NULL OR client_id = @ClientId);
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.QuerySingleOrDefaultAsync<ChatMessage>(new CommandDefinition(
            sql, new { InstructionId = instructionId, ClientId = clientId }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<ChatMessage>> GetMessagesAsync(
        long conversationId,
        long? clientId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string sql = """
            SELECT i.id, i.datetime, i.inst_category_id, i.inst_type_id, i.instruction, i.status,
                   i.insert_date, i.insert_user, i.client_id, i.client_auth_user_id, i.instruction_id,
                   i.completed, i.attachment_id, i.inst_channel,
                   CASE
                       WHEN i.client_auth_user_id IS NOT NULL THEN COALESCE(cu.full_name, cu.user_name, 'Unknown Client User')
                       ELSE COALESCE(au.full_name, au.user_name, 'Unknown Admin User')
                   END AS SenderName
            FROM digital.instructions i
            LEFT JOIN admin.users au ON i.insert_user = au.id AND i.client_auth_user_id IS NULL
            LEFT JOIN internal.support_users cu ON i.client_auth_user_id = cu.id
            WHERE i.instruction_id = @ConversationId
              AND (@ClientId IS NULL OR i.client_id = @ClientId)
            ORDER BY i.datetime ASC;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.QueryAsync<ChatMessage>(new CommandDefinition(
            sql, new { ConversationId = conversationId, ClientId = clientId }, cancellationToken: cancellationToken));
    }
}
