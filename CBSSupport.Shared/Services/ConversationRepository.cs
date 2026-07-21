using CBSSupport.Shared.Contracts;
using Dapper;
using Npgsql;

namespace CBSSupport.Shared.Services;

public sealed class ConversationRepository(string connectionString) : IConversationRepository
{
    private const string SelectColumns = """
        SELECT root.id AS ConversationId,
               root.client_id AS ClientId,
               root.inst_type_id AS InstructionTypeId,
               root.inst_category_id AS InstructionCategoryId
        FROM digital.instructions root
        """;

    public async Task<ConversationAccess?> GetForAdminAsync(
        long conversationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = SelectColumns + "\n" + """
            WHERE root.id = @ConversationId
              AND root.instruction_id = root.id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        var command = new CommandDefinition(
            sql,
            new { ConversationId = conversationId },
            cancellationToken: cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ConversationAccess>(command);
    }

    public async Task<ConversationAccess?> GetForClientAsync(
        long conversationId,
        long clientId,
        CancellationToken cancellationToken = default)
    {
        const string sql = SelectColumns + "\n" + """
            WHERE root.id = @ConversationId
              AND root.instruction_id = root.id
              AND root.client_id = @ClientId
              AND root.inst_type_id <> 105;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        var command = new CommandDefinition(
            sql,
            new { ConversationId = conversationId, ClientId = clientId },
            cancellationToken: cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ConversationAccess>(command);
    }

    public Task<long?> InsertMessageForAdminAsync(
        long conversationId,
        int userId,
        string text,
        DateTime sentAt,
        string? ipAddress,
        CancellationToken cancellationToken = default) =>
        InsertMessageAsync(
            conversationId,
            clientId: null,
            userId,
            text,
            sentAt,
            ipAddress,
            isAdmin: true,
            cancellationToken);

    public Task<long?> InsertMessageForClientAsync(
        long conversationId,
        long clientId,
        int userId,
        string text,
        DateTime sentAt,
        string? ipAddress,
        CancellationToken cancellationToken = default) =>
        InsertMessageAsync(
            conversationId,
            clientId,
            userId,
            text,
            sentAt,
            ipAddress,
            isAdmin: false,
            cancellationToken);

    private async Task<long?> InsertMessageAsync(
        long conversationId,
        long? clientId,
        int userId,
        string text,
        DateTime sentAt,
        string? ipAddress,
        bool isAdmin,
        CancellationToken cancellationToken)
    {
        const string adminSql = """
            INSERT INTO digital.instructions (
                datetime, inst_category_id, inst_type_id, instruction,
                status, insert_user, client_auth_user_id, client_id,
                service_id, ip_address, inst_channel, instruction_id)
            SELECT
                @SentAt, root.inst_category_id, root.inst_type_id, @Text,
                TRUE, @UserId, NULL, root.client_id,
                3, @IpAddress, 'chat', root.id
            FROM digital.instructions root
            WHERE root.id = @ConversationId
              AND root.instruction_id = root.id
            RETURNING id;
            """;

        const string clientSql = """
            INSERT INTO digital.instructions (
                datetime, inst_category_id, inst_type_id, instruction,
                status, insert_user, client_auth_user_id, client_id,
                service_id, ip_address, inst_channel, instruction_id)
            SELECT
                @SentAt, root.inst_category_id, root.inst_type_id, @Text,
                TRUE, @UserId, @UserId, root.client_id,
                3, @IpAddress, 'chat', root.id
            FROM digital.instructions root
            WHERE root.id = @ConversationId
              AND root.instruction_id = root.id
              AND root.client_id = @ClientId
              AND root.inst_type_id <> 105
            RETURNING id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        var command = new CommandDefinition(
            isAdmin ? adminSql : clientSql,
            new
            {
                ConversationId = conversationId,
                ClientId = clientId,
                UserId = userId,
                Text = text,
                SentAt = sentAt,
                IpAddress = ipAddress
            },
            cancellationToken: cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<long?>(command);
    }
}
