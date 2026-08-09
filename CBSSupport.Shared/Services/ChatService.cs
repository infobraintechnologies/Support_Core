using CBSSupport.Shared.Models;
using CBSSupport.Shared.ViewModels;
using CBSSupport.Shared.Contracts;
using Dapper;
using Npgsql;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;

namespace CBSSupport.Shared.Services
{
    public class ChatService : IChatService
    {
        private readonly string _connectionString;
        private readonly ILogger<ChatService> _logger;

        public ChatService(string connectionString, ILogger<ChatService> logger)
        {
            _connectionString = connectionString;
            _logger = logger;
            DefaultTypeMap.MatchNamesWithUnderscores = true;
        }

        // In your ChatService class

        //public async Task<ChatMessage> CreateInstructionTicketAsync(ChatMessage newTicket)
        //{
        //    // --- Step 1: SQL to insert the new record and get back its new ID. ---
        //    var sqlInsert = @"
        //INSERT INTO digital.instructions (
        //    datetime, inst_category_id, inst_type_id, instruction,
        //    status, insert_user, client_auth_user_id, client_id,
        //    service_id, ip_address, geo_location, inst_channel,
        //    attachment_id, instruction_id, remarks, expiry_date
        //)
        //VALUES (
        //    @DateTime, @InstCategoryId, @InstTypeId, @Instruction,
        //    @Status, @InsertUser, @ClientAuthUserId, @ClientId,
        //    @ServiceId, @IpAddress, @GeoLocation, @InstChannel,
        //    @AttachmentId, @InstructionId, @Remarks, @ExpiryDate
        //)
        //RETURNING id;"; // We only need the ID from this query.

        //    // --- Step 2: SQL to handle creating a new conversation group. ---
        //    var sqlUpdate = @"UPDATE digital.instructions SET instruction_id = @Id WHERE id = @Id;";

        //    // --- Step 3: SQL to fetch the full, final record. ---
        //    var sql = @"
        //SELECT 
        //    i.*,
        //    COALESCE(u.full_name, u.user_name, 'Unknown User') AS SenderName
        //FROM digital.instructions i
        //LEFT JOIN internal.support_users u ON i.insert_user = u.id
        //WHERE i.instruction_id = @ConversationId
        //ORDER BY i.datetime ASC;";

        //    using (var connection = new NpgsqlConnection(_connectionString))
        //    {
        //        // This single Dapper call now executes the entire transaction.
        //        var savedMessage = await connection.QueryFirstOrDefaultAsync<ChatMessage>(sql, newTicket);
        //        return savedMessage;
        //    }

        //}

        public async Task<ChatMessage?> CreateInstructionTicketAsync(
            ChatMessage newTicket,
            CancellationToken cancellationToken = default)
        {
            // Ticket and Inquiry roots/replies are Messaging V2 commands. Keeping this
            // legacy compatibility insert limited to non-case conversations prevents an
            // unsequenced, non-outboxed case write from bypassing IConversationService.
            if (ConversationTypes.IsCase(newTicket.InstTypeId)
                || newTicket.InstCategoryId is InstructionCategories.Ticket or InstructionCategories.Inquiry)
            {
                _logger.LogWarning(
                    "Rejected legacy case insert for instruction type {InstructionTypeId} and category {InstructionCategoryId}",
                    newTicket.InstTypeId,
                    newTicket.InstCategoryId);
                return null;
            }

            if (newTicket.InsertUser.HasValue == newTicket.ClientAuthUserId.HasValue)
            {
                return null;
            }

            const string sqlInsert = @"
                INSERT INTO digital.instructions (
                    datetime, inst_category_id, inst_type_id, instruction,
                    status, insert_user, client_auth_user_id, client_id,
                    service_id, ip_address, geo_location, inst_channel,
                    attachment_id, instruction_id, remarks, expiry_date,
                    client_message_id, conversation_sequence
                )
                SELECT
                    @DateTime, @InstCategoryId, @InstTypeId, @Instruction,
                    @Status, @InsertUser, @ClientAuthUserId, @ClientId,
                    @ServiceId, @IpAddress, @GeoLocation, @InstChannel,
                    @AttachmentId, @InstructionId, @Remarks, @ExpiryDate,
                    @ClientMessageId, @ConversationSequence
                WHERE @ClientAuthUserId IS NULL
                   OR EXISTS (
                        SELECT 1
                        FROM internal.support_users authenticated_client
                        WHERE authenticated_client.id = @ClientAuthUserId
                          AND authenticated_client.client_id = @ClientId
                          AND authenticated_client.status IS TRUE
                          AND authenticated_client.deactive_date IS NULL)
                RETURNING id;";

            const string sqlInsertReply = @"
                INSERT INTO digital.instructions (
                    datetime, inst_category_id, inst_type_id, instruction,
                    status, insert_user, client_auth_user_id, client_id,
                    service_id, ip_address, geo_location, inst_channel,
                    attachment_id, instruction_id, remarks, expiry_date
                )
                SELECT
                    @DateTime, root.inst_category_id, root.inst_type_id, @Instruction,
                    @Status, @InsertUser, @ClientAuthUserId, root.client_id,
                    @ServiceId, @IpAddress, @GeoLocation, @InstChannel,
                    @AttachmentId, root.id, @Remarks, @ExpiryDate
                FROM digital.instructions root
                WHERE root.id = @InstructionId
                  AND root.instruction_id = root.id
                  AND root.client_id IS NOT DISTINCT FROM @ClientId
                  AND (@ClientAuthUserId IS NULL OR EXISTS (
                        SELECT 1
                        FROM internal.support_users authenticated_client
                        WHERE authenticated_client.id = @ClientAuthUserId
                          AND authenticated_client.client_id = root.client_id
                          AND authenticated_client.status IS TRUE
                          AND authenticated_client.deactive_date IS NULL))
                RETURNING id;";

            const string sqlUpdate = @"
                UPDATE digital.instructions
                SET instruction_id = @Id
                WHERE id = @Id AND client_id IS NOT DISTINCT FROM @ClientId;";

            const string sqlSelect = @"
            SELECT 
                i.id,
                i.datetime,
                i.inst_category_id,
                i.inst_type_id,
                i.instruction,
                i.status,
                i.insert_date,
                i.insert_user,
                i.client_id,
                i.client_auth_user_id,
                i.instruction_id,
                i.completed,
                i.attachment_id,
                i.client_message_id,
                i.conversation_sequence,
                i.inst_channel,
                CASE 
                    WHEN i.client_auth_user_id IS NOT NULL THEN COALESCE(cu.full_name, cu.user_name, 'Unknown Client User')
                    ELSE COALESCE(au.full_name, au.user_name, 'Unknown Admin User')
                END AS SenderName 
            FROM digital.instructions i
            LEFT JOIN admin.users au ON i.insert_user = au.id AND i.client_auth_user_id IS NULL
            LEFT JOIN internal.support_users cu ON i.client_auth_user_id = cu.id
            WHERE i.id = @Id
              AND i.client_id IS NOT DISTINCT FROM @ClientId;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            if (newTicket.InstructionId is > 0)
            {
                var replyCommand = new CommandDefinition(
                    sqlInsertReply,
                    newTicket,
                    cancellationToken: cancellationToken);
                var replyId = await connection.QuerySingleOrDefaultAsync<long?>(replyCommand);
                if (replyId is null)
                {
                    return null;
                }

                var replySelect = new CommandDefinition(
                    sqlSelect,
                    new { Id = replyId.Value, newTicket.ClientId },
                    cancellationToken: cancellationToken);
                return await connection.QuerySingleOrDefaultAsync<ChatMessage>(replySelect);
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var insertCommand = new CommandDefinition(
                    sqlInsert,
                    newTicket,
                    transaction,
                    cancellationToken: cancellationToken);
                var newId = await connection.QuerySingleOrDefaultAsync<long?>(insertCommand);
                if (newId is null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return null;
                }

                var updateCommand = new CommandDefinition(
                    sqlUpdate,
                    new { Id = newId.Value, newTicket.ClientId },
                    transaction,
                    cancellationToken: cancellationToken);
                await connection.ExecuteAsync(updateCommand);

                var selectCommand = new CommandDefinition(
                    sqlSelect,
                    new { Id = newId.Value, newTicket.ClientId },
                    transaction,
                    cancellationToken: cancellationToken);
                var savedMessage = await connection.QuerySingleOrDefaultAsync<ChatMessage>(selectCommand);

                await transaction.CommitAsync(cancellationToken);
                return savedMessage;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        public async Task<ChatMessage?> CreateGroupChatMessageAsync(ChatMessage newMessage)
        {
            if (newMessage.InsertUser.HasValue == newMessage.ClientAuthUserId.HasValue)
            {
                return null;
            }

            var sqlInsert = @"
            INSERT INTO digital.instructions (
                datetime, inst_category_id, inst_type_id, instruction,
                status, insert_user, client_auth_user_id, client_id,
                service_id, ip_address, geo_location, inst_channel,
                attachment_id, instruction_id, remarks, expiry_date
            )
            SELECT
                @DateTime, @InstCategoryId, @InstTypeId, @Instruction,
                @Status, @InsertUser, @ClientAuthUserId, @ClientId,
                @ServiceId, @IpAddress, @GeoLocation, @InstChannel,
                @AttachmentId, NULL, @Remarks, @ExpiryDate
            WHERE @ClientAuthUserId IS NULL
               OR EXISTS (
                    SELECT 1
                    FROM internal.support_users authenticated_client
                    WHERE authenticated_client.id = @ClientAuthUserId
                      AND authenticated_client.client_id = @ClientId
                      AND authenticated_client.status IS TRUE
                      AND authenticated_client.deactive_date IS NULL)
            RETURNING id;";

            var sqlUpdate = @"UPDATE digital.instructions SET instruction_id = @Id WHERE id = @Id;";

            var sqlSelect = @"
            SELECT 
                i.*,
                CASE 
                    WHEN i.client_auth_user_id IS NOT NULL THEN COALESCE(cu.full_name, cu.user_name, 'Unknown Client User')
                    ELSE COALESCE(au.full_name, au.user_name, 'Unknown Admin User')
                END AS SenderName 
            FROM digital.instructions i
            LEFT JOIN admin.users au ON i.insert_user = au.id AND i.client_auth_user_id IS NULL
            LEFT JOIN internal.support_users cu ON i.client_auth_user_id = cu.id
            WHERE i.id = @Id;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                var newId = await connection.QuerySingleOrDefaultAsync<long?>(sqlInsert, newMessage);
                if (newId is null)
                {
                    return null;
                }

                await connection.ExecuteAsync(sqlUpdate, new { Id = newId.Value });

                var savedMessage = await connection.QuerySingleOrDefaultAsync<ChatMessage>(sqlSelect, new { Id = newId.Value });

                if (savedMessage != null)
                {
                    savedMessage.InstructionId = newId.Value;
                }

                return savedMessage;
            }
        }

        public async Task<long?> GetOrCreateGroupChatConversationIdAsync(long clientId, int clientAuthUserId)
        {
            var sql = @"
        SELECT i.instruction_id 
        FROM digital.instructions i
        WHERE i.client_id = @ClientId 
          AND i.inst_type_id = 100 
          AND i.inst_category_id = 100
          AND i.instruction_id IS NOT NULL
        ORDER BY i.datetime DESC
        LIMIT 1;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                var existingConversationId = await connection.QueryFirstOrDefaultAsync<long?>(sql, new { ClientId = clientId });

                if (existingConversationId.HasValue)
                {
                    return existingConversationId.Value;
                }

                var newGroupChatMessage = new ChatMessage
                {
                    DateTime = DateTime.UtcNow,
                    InstTypeId = 100,
                    InstCategoryId = 100,
                    Instruction = "Group chat conversation started",
                    Status = true,
                    InsertUser = null,
                    ClientId = clientId,
                    ClientAuthUserId = clientAuthUserId,
                    InstChannel = "chat",
                    Remarks = "System generated group chat conversation"
                };

                var createdMessage = await CreateGroupChatMessageAsync(newGroupChatMessage);
                return createdMessage is null
                    ? null
                    : createdMessage.InstructionId ?? createdMessage.Id;
            }
        }


        public async Task<CasePage<TicketViewModel>> ListTicketsAsync(
            CaseListCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            const string select = @"
        SELECT
            i.id AS Id,
            COALESCE(public.try_get_json_value(i.remarks, 'subject'), 'General Support') AS Subject,
            i.datetime AS Date,
            u.full_name AS CreatedBy,
            res.full_name AS ResolvedBy,
            CASE WHEN COALESCE(i.completed, false) THEN 'Resolved' ELSE 'Open' END AS Status,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            i.instruction AS Description,
            i.remarks AS Remarks,
            i.expiry_date::timestamp without time zone AS ExpiryDate,
            i.completed_on AS ResolvedDate,
            i.client_id AS ClientId,
            i.inst_type_id AS InstTypeId,
            ca.version AS Version
        FROM digital.instructions i
        JOIN digital.conversation_access ca ON ca.conversation_id = i.id
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users res ON i.completed_by = res.id
        WHERE i.inst_category_id = 101
          AND i.inst_type_id BETWEEN 110 AND 117
          AND i.instruction_id = i.id
          AND (@ClientId IS NULL OR i.client_id = @ClientId)
          AND (@IsCompleted IS NULL OR COALESCE(i.completed, false) = @IsCompleted)
          AND (@TypeCode IS NULL OR i.inst_type_id = @TypeCode)
          AND (@Priority IS NULL OR COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') = @Priority)";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = (await connection.QueryAsync<TicketViewModel>(new CommandDefinition(
                select + BuildCaseCursorPredicate(criteria) + BuildCaseOrderBy(criteria) + "\nLIMIT @Take;",
                CaseListParameters(criteria),
                cancellationToken: cancellationToken))).AsList();
            return ToCasePage(rows, criteria, ToTicketCursor);
        }

        public async Task<CasePage<InquiryViewModel>> ListInquiriesAsync(
            CaseListCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            const string select = @"
        SELECT
            i.id AS Id,
            COALESCE(t.inst_type_name, 'Unknown Topic') AS Topic,
            COALESCE(au.full_name, u.full_name, 'Unknown') AS InquiredBy,
            i.datetime AS Date,
            CASE WHEN COALESCE(i.completed, false) THEN 'Completed' ELSE 'Pending' END AS Outcome,
            i.instruction AS Description,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            i.completed_on AS ResolvedDate,
            i.client_id AS ClientId,
            i.inst_type_id AS InstTypeId,
            ca.version AS Version
        FROM digital.instructions i
        JOIN digital.conversation_access ca ON ca.conversation_id = i.id
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users au ON i.insert_user = au.id
        LEFT JOIN digital.inst_types t ON i.inst_type_id = t.id
        WHERE i.inst_category_id = 102
          AND i.inst_type_id IN (121, 122)
          AND i.instruction_id = i.id
          AND (@ClientId IS NULL OR i.client_id = @ClientId)
          AND (@IsCompleted IS NULL OR COALESCE(i.completed, false) = @IsCompleted)
          AND (@TypeCode IS NULL OR i.inst_type_id = @TypeCode)
          AND (@Priority IS NULL OR COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') = @Priority)";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = (await connection.QueryAsync<InquiryViewModel>(new CommandDefinition(
                select + BuildCaseCursorPredicate(criteria) + BuildCaseOrderBy(criteria) + "\nLIMIT @Take;",
                CaseListParameters(criteria),
                cancellationToken: cancellationToken))).AsList();
            return ToCasePage(rows, criteria, ToInquiryCursor);
        }

        public Task<IEnumerable<TicketViewModel>> GetTicketsByClientIdAsync(long clientId) =>
            GetTicketsByClientIdAsync(clientId, CancellationToken.None);

        public async Task<IEnumerable<TicketViewModel>> GetTicketsByClientIdAsync(
            long clientId,
            CancellationToken cancellationToken)
        {
            var sql = @"
        SELECT 
            i.id AS Id,
            COALESCE(public.try_get_json_value(i.remarks, 'subject'), 'General Support') AS Subject,
            i.datetime AS Date,
            u.full_name AS CreatedBy,
            res.full_name AS ResolvedBy,
            CASE WHEN i.completed = true THEN 'Resolved' ELSE 'Open' END AS Status,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            i.instruction AS Description,  
            i.remarks AS Remarks,  
            i.expiry_date::timestamp without time zone AS ExpiryDate,
            i.completed_on AS ResolvedDate,
            i.client_id AS ClientId,
            i.inst_type_id AS InstTypeId,
            ca.version AS Version
        FROM digital.instructions i
        JOIN digital.conversation_access ca ON ca.conversation_id = i.id
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users res ON i.completed_by = res.id
        WHERE i.client_id = @ClientId
          AND i.inst_category_id = 101
          AND i.instruction_id = i.id
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<TicketViewModel>(new CommandDefinition(
                    sql,
                    new { ClientId = clientId },
                    cancellationToken: cancellationToken));
            }
        }

        public Task<IEnumerable<InquiryViewModel>> GetInquiriesByClientIdAsync(long clientId) =>
            GetInquiriesByClientIdAsync(clientId, CancellationToken.None);

        public async Task<IEnumerable<InquiryViewModel>> GetInquiriesByClientIdAsync(
            long clientId,
            CancellationToken cancellationToken)
        {
            var sql = @"
        SELECT
            i.id AS Id,
            t.inst_type_name AS Topic,
            u.full_name AS InquiredBy,
            i.datetime AS Date,
            CASE 
                WHEN i.completed = true THEN 'Completed'
                ELSE 'Pending'
            END AS Outcome,
            i.instruction AS Description,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            i.completed_on AS ResolvedDate,
            i.client_id AS ClientId,
            i.inst_type_id AS InstTypeId,
            ca.version AS Version
        FROM digital.instructions i
        JOIN digital.conversation_access ca ON ca.conversation_id = i.id
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN digital.inst_types t ON i.inst_type_id = t.id
        WHERE i.client_id = @ClientId
          AND i.inst_category_id = 102
          AND i.instruction_id = i.id
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<InquiryViewModel>(new CommandDefinition(
                    sql,
                    new { ClientId = clientId },
                    cancellationToken: cancellationToken));
            }
        }

        public async Task<IEnumerable<ClientUser>> GetAllClientsAsync()
        {
            var sql = @"
        SELECT DISTINCT ON (client_id)
            client_id,
            full_name,
            user_name
        FROM internal.support_users
        ORDER BY client_id, full_name;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<ClientUser>(sql);
            }
        }

        public Task<IEnumerable<TicketViewModel>> GetAllTicketsAsync() =>
            GetAllTicketsAsync(CancellationToken.None);

        public async Task<IEnumerable<TicketViewModel>> GetAllTicketsAsync(CancellationToken cancellationToken)
        {
            var sql = @"
        SELECT 
            i.id AS Id,
            COALESCE(public.try_get_json_value(i.remarks, 'subject'), 'General Support') AS Subject,
            i.datetime AS Date,
            u.full_name AS CreatedBy,
            res.full_name AS ResolvedBy,
            CASE WHEN i.completed = true THEN 'Resolved' ELSE 'Open' END AS Status,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            COALESCE(u.full_name, 'Unknown Client') AS ClientName,
            i.instruction AS Description,
            i.remarks AS Remarks,
            i.expiry_date::timestamp without time zone AS ExpiryDate,
            i.completed_on AS ResolvedDate,
            i.client_id AS ClientId,
            i.inst_type_id AS InstTypeId,
            ca.version AS Version
        FROM digital.instructions i
        JOIN digital.conversation_access ca ON ca.conversation_id = i.id
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users res ON i.completed_by = res.id
        WHERE i.inst_category_id = 101
          AND i.instruction_id = i.id
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<TicketViewModel>(new CommandDefinition(
                    sql,
                    cancellationToken: cancellationToken));
            }
        }
        public Task<IEnumerable<InquiryViewModel>> GetAllInquiriesAsync() =>
            GetAllInquiriesAsync(CancellationToken.None);

        public async Task<IEnumerable<InquiryViewModel>> GetAllInquiriesAsync(CancellationToken cancellationToken)
        {
            var inquiryTypeIds = new[] { 121, 122 };

            var sql = @"
        SELECT
            i.id AS Id,
            COALESCE(t.inst_type_name, 'Unknown Topic') AS Topic,
            COALESCE(au.full_name, u.full_name, 'Unknown') AS InquiredBy,
            i.datetime AS Date,
            CASE 
                WHEN i.completed = true THEN 'Completed'
                ELSE 'Pending'
            END AS Outcome,
            COALESCE(u.full_name, au.full_name, 'Unknown Client') AS ClientName,
            i.instruction AS Description,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            i.completed_on AS ResolvedDate,
            i.client_id AS ClientId,
            i.inst_type_id AS InstTypeId,
            ca.version AS Version
        FROM digital.instructions i
        JOIN digital.conversation_access ca ON ca.conversation_id = i.id
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users au ON i.insert_user = au.id
        LEFT JOIN digital.inst_types t ON i.inst_type_id = t.id
        WHERE i.inst_type_id = ANY(@InquiryTypeIds)
          AND i.instruction_id = i.id
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                var result = (await connection.QueryAsync<InquiryViewModel>(new CommandDefinition(
                    sql,
                    new { InquiryTypeIds = inquiryTypeIds },
                    cancellationToken: cancellationToken))).ToList();
                _logger.LogDebug("Loaded {RecordCount} inquiries", result.Count);
                return result;
            }
        }

        public async Task<DashboardStatsViewModel> GetDashboardStatsAsync()
        {
            var sql = @"
        SELECT 
            COUNT(*) FILTER (WHERE i.inst_category_id = 101 AND i.instruction_id = i.id) AS TotalTickets,
            COUNT(*) FILTER (WHERE i.inst_category_id = 101 AND i.instruction_id = i.id AND i.completed = false) AS OpenTickets,
            COUNT(*) FILTER (WHERE i.inst_category_id = 101 AND i.instruction_id = i.id AND i.completed = true) AS ResolvedTickets,
            COUNT(*) FILTER (WHERE i.inst_category_id = 102 AND i.instruction_id = i.id) AS TotalInquiries
        FROM digital.instructions i;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryFirstOrDefaultAsync<DashboardStatsViewModel>(sql);
            }
        }

        public async Task<IEnumerable<TicketViewModel>> GetSolvedTicketsAsync()
        {
            var ticketTypeIds = Enumerable.Range(110, 8).ToArray();
            var sql = @"
        SELECT 
            i.id AS Id,
            i.instruction AS Subject,
            i.datetime AS Date,
            u.full_name AS CreatedBy,
            res.full_name AS ResolvedBy,
            CASE WHEN i.completed = true THEN 'Resolved' ELSE 'Open' END AS Status,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            COALESCE(u.full_name, 'Unknown Client') AS ClientName
        FROM digital.instructions i
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users res ON i.completed_by = res.id
        WHERE i.inst_type_id = ANY(@TicketTypeIds)
          AND i.instruction_id = i.id
          AND i.completed = true
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<TicketViewModel>(sql, new { TicketTypeIds = ticketTypeIds });
            }
        }

        public async Task<IEnumerable<InquiryViewModel>> GetSolvedInquiriesAsync()
        {
            var inquiryTypeIds = new[] { 121, 122 };
            var sql = @"
        SELECT
            i.id AS Id,
            t.inst_type_name AS Topic,
            COALESCE(au.full_name, u.full_name, 'Unknown') AS InquiredBy,
            i.datetime AS Date,
            CASE 
                WHEN i.completed = true THEN 'Completed'
                ELSE 'Pending'
            END AS Outcome,
            COALESCE(u.full_name, au.full_name, 'Unknown Client') AS ClientName,
            i.client_id AS ClientId,
            i.instruction AS Description,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            i.completed_on AS ResolvedDate
        FROM digital.instructions i
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users au ON i.insert_user = au.id
        LEFT JOIN digital.inst_types t ON i.inst_type_id = t.id
        WHERE i.inst_type_id = ANY(@InquiryTypeIds)
          AND i.instruction_id = i.id
          AND i.completed = true
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<InquiryViewModel>(sql, new { InquiryTypeIds = inquiryTypeIds });
            }
        }

        public async Task<IEnumerable<TicketViewModel>> GetUnsolvedTicketsAsync()
        {
            var ticketTypeIds = Enumerable.Range(110, 8).ToArray();
            var sql = @"
        SELECT 
            i.id AS Id,
            i.instruction AS Subject,
            i.datetime AS Date,
            u.full_name AS CreatedBy,
            res.full_name AS ResolvedBy,
            CASE WHEN i.completed = true THEN 'Resolved' ELSE 'Open' END AS Status,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            COALESCE(u.full_name, 'Unknown Client') AS ClientName
        FROM digital.instructions i
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users res ON i.completed_by = res.id
        WHERE i.inst_type_id = ANY(@TicketTypeIds)
          AND i.instruction_id = i.id
          AND (i.completed = false OR i.completed IS NULL)
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<TicketViewModel>(sql, new { TicketTypeIds = ticketTypeIds });
            }
        }

        public async Task<IEnumerable<InquiryViewModel>> GetUnsolvedInquiriesAsync()
        {
            var inquiryTypeIds = new[] { 121, 122 };
            var sql = @"
        SELECT
            i.id AS Id,
            t.inst_type_name AS Topic,
            COALESCE(au.full_name, u.full_name, 'Unknown') AS InquiredBy,
            i.datetime AS Date,
            CASE 
                WHEN i.completed = true THEN 'Completed'
                ELSE 'Pending'
            END AS Outcome,
            COALESCE(u.full_name, au.full_name, 'Unknown Client') AS ClientName,
            i.client_id AS ClientId,
            i.instruction AS Description,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            i.completed_on AS ResolvedDate
        FROM digital.instructions i
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users au ON i.insert_user = au.id
        LEFT JOIN digital.inst_types t ON i.inst_type_id = t.id
        WHERE i.inst_type_id = ANY(@InquiryTypeIds)
          AND i.instruction_id = i.id
          AND (i.completed = false OR i.completed IS NULL)
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<InquiryViewModel>(sql, new { InquiryTypeIds = inquiryTypeIds });
            }
        }

        public Task<TicketViewModel?> GetTicketDetailsByIdAsync(long ticketId, long? clientId = null) =>
            GetTicketDetailsByIdAsync(ticketId, clientId, CancellationToken.None);

        public async Task<TicketViewModel?> GetTicketDetailsByIdAsync(
            long ticketId,
            long? clientId,
            CancellationToken cancellationToken)
        {
            var sql = @"
        SELECT 
            i.id AS Id,
            COALESCE(public.try_get_json_value(i.remarks, 'subject'), 'General Support') AS Subject,
            i.datetime AS Date,
            u.full_name AS CreatedBy,
            res.full_name AS ResolvedBy,
            CASE WHEN i.completed = true THEN 'Resolved' ELSE 'Open' END AS Status,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            i.instruction AS Description,
            i.remarks AS Remarks,
            i.expiry_date::timestamp without time zone AS ExpiryDate,
            i.completed_on AS ResolvedDate,
            COALESCE(u.full_name, 'Unknown Client') AS ClientName,
            i.client_id AS ClientId,
            i.inst_type_id AS InstTypeId,
            ca.version AS Version
        FROM digital.instructions i
        JOIN digital.conversation_access ca ON ca.conversation_id = i.id
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users res ON i.completed_by = res.id
        WHERE i.id = @TicketId
          AND i.inst_category_id = 101
          AND (@ClientId IS NULL OR i.client_id = @ClientId)";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QuerySingleOrDefaultAsync<TicketViewModel>(new CommandDefinition(
                    sql,
                    new { TicketId = ticketId, ClientId = clientId },
                    cancellationToken: cancellationToken));
            }
        }

        public Task<InquiryViewModel?> GetInquiryDetailsByIdAsync(long inquiryId, long? clientId = null) =>
            GetInquiryDetailsByIdAsync(inquiryId, clientId, CancellationToken.None);

        public async Task<InquiryViewModel?> GetInquiryDetailsByIdAsync(
            long inquiryId,
            long? clientId,
            CancellationToken cancellationToken)
        {
            var sql = @"
        SELECT
            i.id AS Id,
            COALESCE(t.inst_type_name, 'Unknown Topic') AS Topic,
            COALESCE(au.full_name, u.full_name, 'Unknown') AS InquiredBy,
            i.datetime AS Date,
            CASE 
                WHEN i.completed = true THEN 'Completed'
                ELSE 'Pending'
            END AS Outcome,
            COALESCE(u.full_name, au.full_name, 'Unknown Client') AS ClientName,
            i.client_id AS ClientId,
            i.instruction AS Description,
            COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal') AS Priority,
            i.completed_on AS ResolvedDate,
            i.inst_type_id AS InstTypeId,
            ca.version AS Version
        FROM digital.instructions i
        JOIN digital.conversation_access ca ON ca.conversation_id = i.id
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users au ON i.insert_user = au.id
        LEFT JOIN digital.inst_types t ON i.inst_type_id = t.id
        WHERE i.id = @InquiryId
          AND i.inst_category_id = 102
          AND (@ClientId IS NULL OR i.client_id = @ClientId)";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QuerySingleOrDefaultAsync<InquiryViewModel>(new CommandDefinition(
                    sql,
                    new { InquiryId = inquiryId, ClientId = clientId },
                    cancellationToken: cancellationToken));
            }
        }

        public async Task<IEnumerable<object>> GetUnreadNotificationsForAdminAsync()
        {
            var sql = @"
        SELECT i.id, i.instruction, i.inst_category_id, i.insert_date, i.datetime,
               i.notification_seen_by_admin, i.client_id, i.client_auth_user_id,
               CASE 
                   WHEN i.client_auth_user_id IS NOT NULL THEN COALESCE(cu.full_name, cu.user_name, 'Unknown Client User')
                   ELSE COALESCE(au.full_name, au.user_name, 'Unknown Admin User')
               END as senderName
        FROM digital.instructions i
        LEFT JOIN internal.support_users cu ON i.client_auth_user_id = cu.id
        LEFT JOIN admin.users au ON i.insert_user = au.id
        WHERE i.notification_seen_by_admin = 0 
        AND i.inst_category_id IN (100, 101, 102)
        ORDER BY i.insert_date DESC
        LIMIT 50";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                var notifications = await connection.QueryAsync(sql);
                return notifications;
            }
        }

        public async Task<bool> MarkNotificationSeenByAdminAsync(long instructionId)
        {
            var sql = @"
            UPDATE digital.instructions 
            SET notification_seen_by_admin = 1 
            WHERE id = @InstructionId";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                var rowsAffected = await connection.ExecuteAsync(sql, new { InstructionId = instructionId });
                return rowsAffected > 0;
            }
        }

        public async Task<int> MarkAllNotificationsSeenByAdminAsync()
        {
            var sql = @"
            UPDATE digital.instructions 
            SET notification_seen_by_admin = 1 
            WHERE notification_seen_by_admin = 0 
            AND inst_category_id IN (100, 101, 102)";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                var rowsAffected = await connection.ExecuteAsync(sql);
                return rowsAffected;
            }
        }

        public async Task<bool> MarkNotificationSeenByClientAsync(long instructionId, long clientId)
        {
            const string query = @"
        UPDATE digital.instructions 
        SET notification_seen_by_client = 1,
            edit_date = @EditDate
        WHERE id = @InstructionId
          AND client_id = @ClientId";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var rowsAffected = await connection.ExecuteAsync(query, new
            {
                InstructionId = instructionId,
                ClientId = clientId,
                EditDate = DateTime.UtcNow
            });

            return rowsAffected > 0;
        }

        public async Task<IEnumerable<object>> GetUnreadNotificationsForClientAsync(long clientId)
        {
            const string query = @"
        SELECT 
            i.id,
            i.instruction,
            i.datetime,
            i.insert_date,
            i.inst_category_id,
            i.notification_seen_by_client,
            COALESCE(ca.full_name, ca.user_name, 'Support') as sendername,
            i.instruction_id
        FROM digital.instructions i
        LEFT JOIN admin.users ca ON i.insert_user = ca.id
        WHERE i.client_id = @ClientId 
        AND (i.notification_seen_by_client IS NULL OR i.notification_seen_by_client = 0)
        ORDER BY i.insert_date DESC
        LIMIT 50";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var result = await connection.QueryAsync(query, new { ClientId = clientId });
            return result.Cast<object>();
        }

        public async Task<int> MarkAllNotificationsSeenByClientAsync(long clientId)
        {
            const string query = @"
        UPDATE digital.instructions 
        SET notification_seen_by_client = 1,
            edit_date = @EditDate
        WHERE client_id = @ClientId 
        AND (notification_seen_by_client IS NULL OR notification_seen_by_client = 0)";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var rowsAffected = await connection.ExecuteAsync(query, new
            {
                ClientId = clientId,
                EditDate = DateTime.UtcNow
            });

            return rowsAffected;
        }

        private static object CaseListParameters(CaseListCriteria criteria) => new
        {
            criteria.ClientId,
            criteria.IsCompleted,
            criteria.TypeCode,
            criteria.Priority,
            CursorSortValue = GetCursorSortValue(criteria),
            CursorId = criteria.Cursor?.Id,
            Take = checked(criteria.PageSize + 1)
        };

        private static object? GetCursorSortValue(CaseListCriteria criteria) => criteria.Sort switch
        {
            CasePagination.CreatedAtSort => criteria.Cursor?.CreatedAt,
            CasePagination.StatusSort => criteria.Cursor?.StatusRank,
            CasePagination.TypeSort => criteria.Cursor?.TypeCode,
            CasePagination.PrioritySort => criteria.Cursor?.Priority,
            _ => null
        };

        private static string BuildCaseCursorPredicate(CaseListCriteria criteria)
        {
            if (criteria.Cursor is null)
            {
                return string.Empty;
            }

            var comparison = criteria.Direction == CasePagination.Ascending ? ">" : "<";
            var expression = CaseSortExpression(criteria.Sort);
            return $"\n          AND ({expression} {comparison} @CursorSortValue OR ({expression} = @CursorSortValue AND i.id {comparison} @CursorId))";
        }

        private static string BuildCaseOrderBy(CaseListCriteria criteria)
        {
            var direction = criteria.Direction == CasePagination.Ascending ? "ASC" : "DESC";
            return $"\n        ORDER BY {CaseSortExpression(criteria.Sort)} {direction}, i.id {direction}";
        }

        private static string CaseSortExpression(string sort) => sort switch
        {
            CasePagination.CreatedAtSort => "i.datetime",
            CasePagination.StatusSort => "CASE WHEN COALESCE(i.completed, false) THEN 1 ELSE 0 END",
            CasePagination.TypeSort => "i.inst_type_id",
            CasePagination.PrioritySort => "COALESCE(public.try_get_json_value(i.remarks, 'priority'), 'Normal')",
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Unsupported case sort field.")
        };

        private static CasePage<T> ToCasePage<T>(
            IReadOnlyList<T> rows,
            CaseListCriteria criteria,
            Func<T, CaseListCriteria, CaseListCursor> toCursor)
        {
            var hasNextPage = rows.Count > criteria.PageSize;
            var items = rows.Take(criteria.PageSize).ToArray();
            var nextCursor = hasNextPage
                ? CasePagination.EncodeCursor(toCursor(items[^1], criteria))
                : null;
            return new CasePage<T>(items, criteria.PageSize, nextCursor);
        }

        private static CaseListCursor ToTicketCursor(TicketViewModel item, CaseListCriteria criteria) =>
            ToCursor(
                criteria,
                item.Id,
                item.Date,
                string.Equals(item.Status, CaseDtoMapper.TicketResolvedStatus, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                item.InstTypeId,
                item.Priority);

        private static CaseListCursor ToInquiryCursor(InquiryViewModel item, CaseListCriteria criteria) =>
            ToCursor(
                criteria,
                item.Id,
                item.Date,
                string.Equals(item.Outcome, CaseDtoMapper.InquiryCompletedStatus, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                item.InstTypeId,
                item.Priority);

        private static CaseListCursor ToCursor(
            CaseListCriteria criteria,
            long id,
            DateTime createdAt,
            int statusRank,
            short typeCode,
            string? priority) =>
            new(
                criteria.Sort,
                criteria.Direction,
                id,
                criteria.Sort == CasePagination.CreatedAtSort ? createdAt : null,
                criteria.Sort == CasePagination.StatusSort ? statusRank : null,
                criteria.Sort == CasePagination.TypeSort ? typeCode : null,
                criteria.Sort == CasePagination.PrioritySort
                    ? string.IsNullOrWhiteSpace(priority) ? CasePriorities.Normal : priority.Trim()
                    : null);
    }
}
