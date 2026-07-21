using System.Text.Json;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.ViewModels;
using Dapper;
using Npgsql;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;

namespace CBSSupport.Shared.Services
{
    public class ChatService : IChatService
    {
        private readonly string _connectionString;

        public ChatService(string connectionString)
        {
            _connectionString = connectionString;
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
            if (newTicket.InstCategoryId == 101)
            {
                var priority = newTicket.Priority ?? "Normal";
                var userRemarks = newTicket.Remarks ?? "";

                var subjectMap = new Dictionary<short, string>
                {
                    { 110, "Training" },
                    { 111, "Migration" },
                    { 112, "Setup" },
                    { 113, "Correction" },
                    { 114, "Bug Fix" },
                    { 115, "New Feature Request" },
                    { 116, "Feature Enhancement" },
                    { 117, "Backend Workaround" }
                };

                var subject = subjectMap.TryGetValue(newTicket.InstTypeId, out var mappedSubject)
                    ? mappedSubject
                    : "General Support";

                var remarksJson = new
                {
                    priority = priority,
                    userremarks = userRemarks,
                    subject = subject
                };

                newTicket.Remarks = JsonSerializer.Serialize(remarksJson);
            }

            const string sqlInsert = @"
                INSERT INTO digital.instructions (
                    datetime, inst_category_id, inst_type_id, instruction,
                    status, insert_user, client_auth_user_id, client_id,
                    service_id, ip_address, geo_location, inst_channel,
                    attachment_id, instruction_id, remarks, expiry_date
                )
                VALUES (
                    @DateTime, @InstCategoryId, @InstTypeId, @Instruction,
                    @Status, @InsertUser, @ClientAuthUserId, @ClientId,
                    @ServiceId, @IpAddress, @GeoLocation, @InstChannel,
                    @AttachmentId, @InstructionId, @Remarks, @ExpiryDate
                )
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
                var newId = await connection.QuerySingleAsync<long>(insertCommand);

                var updateCommand = new CommandDefinition(
                    sqlUpdate,
                    new { Id = newId, newTicket.ClientId },
                    transaction,
                    cancellationToken: cancellationToken);
                await connection.ExecuteAsync(updateCommand);

                var selectCommand = new CommandDefinition(
                    sqlSelect,
                    new { Id = newId, newTicket.ClientId },
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

        public async Task<ChatMessage> CreateGroupChatMessageAsync(ChatMessage newMessage)
        {
            var sqlInsert = @"
            INSERT INTO digital.instructions (
                datetime, inst_category_id, inst_type_id, instruction,
                status, insert_user, client_auth_user_id, client_id,
                service_id, ip_address, geo_location, inst_channel,
                attachment_id, instruction_id, remarks, expiry_date
            )
            VALUES (
                @DateTime, @InstCategoryId, @InstTypeId, @Instruction,
                @Status, @InsertUser, @ClientAuthUserId, @ClientId,
                @ServiceId, @IpAddress, @GeoLocation, @InstChannel,
                @AttachmentId, NULL, @Remarks, @ExpiryDate
            )
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
                long newId = await connection.ExecuteScalarAsync<long>(sqlInsert, newMessage);

                await connection.ExecuteAsync(sqlUpdate, new { Id = newId });

                var savedMessage = await connection.QueryFirstOrDefaultAsync<ChatMessage>(sqlSelect, new { Id = newId });

                if (savedMessage != null)
                {
                    savedMessage.InstructionId = newId;
                }

                return savedMessage;
            }
        }

        public async Task<long> GetOrCreateGroupChatConversationIdAsync(long clientId, int loggedInUserId)
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
                    InsertUser = loggedInUserId,
                    ClientId = clientId,
                    ServiceId = 3,
                    InstChannel = "chat",
                    Remarks = "System generated group chat conversation"
                };

                var createdMessage = await CreateGroupChatMessageAsync(newGroupChatMessage);
                return createdMessage.InstructionId ?? createdMessage.Id;
            }
        }

        public async Task<SidebarViewModel> GetSidebarForUserAsync(long fintechUserId, long viewingClientId)
        {
            var sidebar = new SidebarViewModel();

            //using (var connection = new NpgsqlConnection(_connectionString))
            //{
            //    // === 1. Get Private Chats ===
            //    var privateChatSql = @"
            //        SELECT DISTINCT ON (i.instruction_id)
            //            i.instruction_id AS ConversationId,
            //            COALESCE(u.full_name, 'Client User') AS DisplayName,
            //            i.instruction AS Subtitle,
            //            'P' AS AvatarInitials, 'avatar-bg-purple' AS AvatarClass,
            //            'support-private' AS Route
            //        FROM digital.instructions i
            //        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
            //        WHERE i.client_id = @ViewingClientId AND i.inst_type_id = 101 AND i.instruction_id IS NOT NULL
            //        ORDER BY i.instruction_id, i.datetime DESC;";
            //    sidebar.PrivateChats.AddRange(await connection.QueryAsync<SidebarChatItem>(privateChatSql, new { ViewingClientId = viewingClientId }));

            //    // === 2. Get Internal Chats ===
            //    var internalChatSql = @"
            //        SELECT DISTINCT ON (i.instruction_id)
            //            i.instruction_id AS ConversationId,
            //            'Internal Discussion' AS DisplayName,
            //            i.instruction AS Subtitle,
            //            'I' AS AvatarInitials, 'avatar-bg-green' AS AvatarClass,
            //            'internal-team-chat' AS Route
            //        FROM digital.instructions i
            //        WHERE i.client_id = @FintechClientId AND i.inst_type_id = 105 AND i.instruction_id IS NOT NULL
            //        ORDER BY i.instruction_id, i.datetime DESC;";
            //    sidebar.InternalChats.AddRange(await connection.QueryAsync<SidebarChatItem>(internalChatSql, new { FintechClientId = fintechCompanyClientId }));

            //    // === 3. Get Ticket Chats ===
            //    var ticketTypeIds = Enumerable.Range(110, 8).ToArray();
            //    var ticketSql = @"
            //        SELECT DISTINCT ON (i.instruction_id)
            //            i.instruction_id AS ConversationId,
            //            t.inst_type_name AS DisplayName,
            //            i.instruction AS Subtitle,
            //            'T' AS AvatarInitials, 'avatar-bg-orange' AS AvatarClass,
            //            CASE i.inst_type_id
            //                WHEN 110 THEN 'ticket/training' WHEN 111 THEN 'ticket/migration'
            //                WHEN 112 THEN 'ticket/setup' WHEN 113 THEN 'ticket/correction'
            //                WHEN 114 THEN 'ticket/bug-fix' WHEN 115 THEN 'ticket/new-feature'
            //                WHEN 116 THEN 'ticket/feature-enhancement' WHEN 117 THEN 'ticket/backend-workaround'
            //                ELSE ''
            //            END AS Route
            //        FROM digital.instructions i JOIN digital.inst_types t ON i.inst_type_id = t.id
            //        WHERE i.client_id = @ViewingClientId AND i.inst_type_id = ANY(@TicketTypeIds) AND i.instruction_id IS NOT NULL
            //        ORDER BY i.instruction_id, i.datetime DESC;";
            //    var tickets = await connection.QueryAsync<SidebarChatItem>(ticketSql, new { ViewingClientId = viewingClientId, TicketTypeIds = ticketTypeIds });
            //    foreach (var ticket in tickets) { ticket.DisplayName = $"#{ticket.ConversationId} - {ticket.DisplayName}"; }
            //    sidebar.TicketChats.AddRange(tickets);

            //    // === 4. Get Inquiry Chats ===
            //    var inquiryTypeIds = new[] { 121, 122 };
            //    var inquirySql = @"
            //         SELECT DISTINCT ON (i.instruction_id)
            //            i.instruction_id AS ConversationId,
            //            t.inst_type_name AS DisplayName,
            //            i.instruction AS Subtitle,
            //            'Q' AS AvatarInitials, 'avatar-bg-cyan' AS AvatarClass,
            //            CASE i.inst_type_id
            //                WHEN 121 THEN 'inquiry/accounts' WHEN 122 THEN 'inquiry/sales'
            //                ELSE ''
            //            END AS Route
            //        FROM digital.instructions i JOIN digital.inst_types t ON i.inst_type_id = t.id
            //        WHERE i.client_id = @ViewingClientId AND i.inst_type_id = ANY(@InquiryTypeIds) AND i.instruction_id IS NOT NULL
            //        ORDER BY i.instruction_id, i.datetime DESC;";
            //    sidebar.InquiryChats.AddRange(await connection.QueryAsync<SidebarChatItem>(inquirySql, new { ViewingClientId = viewingClientId, InquiryTypeIds = inquiryTypeIds }));
            //}

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                var groupChatSql = @"
        SELECT DISTINCT ON (i.instruction_id)
            i.instruction_id AS ConversationId,
            'Company Group Chat' AS DisplayName,
            COALESCE(i.instruction, 'Start the conversation') AS Subtitle,
            'G' AS AvatarInitials, 
            'admin-avatar-bg-success' AS AvatarClass,
            'support-group' AS Route
        FROM digital.instructions i
        WHERE i.client_id = @ViewingClientId 
          AND i.inst_type_id = 100 
          AND i.inst_category_id = 100
          AND i.instruction_id IS NOT NULL
        ORDER BY i.instruction_id, i.datetime DESC
        LIMIT 1;";

                var groupChats = await connection.QueryAsync<SidebarChatItem>(groupChatSql, new { ViewingClientId = viewingClientId });

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

                if (!groupChats.Any())
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

                var privateChatSql = @"
        SELECT DISTINCT ON (i.instruction_id)
            i.instruction_id AS ConversationId, 
            COALESCE(u.full_name, 'Client User') AS DisplayName,
            i.instruction AS Subtitle,
            'P' AS AvatarInitials, 'admin-avatar-bg-purple' AS AvatarClass,
            'support-private' AS Route
        FROM digital.instructions i
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        WHERE i.client_id = @ViewingClientId 
          AND i.inst_type_id = 101 
          AND i.instruction_id IS NOT NULL
        ORDER BY i.instruction_id, i.datetime DESC;";
                sidebar.PrivateChats.AddRange(await connection.QueryAsync<SidebarChatItem>(privateChatSql, new { ViewingClientId = viewingClientId }));

                var internalChatSql = @"
        SELECT DISTINCT ON (i.instruction_id)
            i.instruction_id AS ConversationId, -- Correctly aliased
            'Internal Discussion' AS DisplayName,
            i.instruction AS Subtitle,
            'I' AS AvatarInitials, 'avatar-bg-green' AS AvatarClass,
            'internal-team-chat' AS Route
        FROM digital.instructions i
        WHERE i.client_id = @ClientId AND i.inst_type_id = 105 AND i.instruction_id IS NOT NULL
        ORDER BY i.instruction_id, i.datetime DESC;";
                sidebar.InternalChats.AddRange(await connection.QueryAsync<SidebarChatItem>(internalChatSql, new { ClientId = viewingClientId }));

                var ticketTypeIds = Enumerable.Range(110, 8).ToArray();
                var ticketSql = @"
        SELECT DISTINCT ON (i.instruction_id)
            i.instruction_id AS ConversationId, -- Correctly aliased
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
        WHERE i.client_id = @ViewingClientId AND i.inst_type_id = ANY(@TicketTypeIds) AND i.instruction_id IS NOT NULL
        ORDER BY i.instruction_id, i.datetime DESC;";
                var tickets = await connection.QueryAsync<SidebarChatItem>(ticketSql, new { ViewingClientId = viewingClientId, TicketTypeIds = ticketTypeIds });
                foreach (var ticket in tickets) { ticket.DisplayName = $"#{ticket.ConversationId} - {ticket.DisplayName}"; }
                sidebar.TicketChats.AddRange(tickets);

                var inquiryTypeIds = new[] { 121, 122 };
                var inquirySql = @"
         SELECT DISTINCT ON (i.instruction_id)
            i.instruction_id AS ConversationId, -- Correctly aliased
            t.inst_type_name AS DisplayName,
            i.instruction AS Subtitle,
            'Q' AS AvatarInitials, 'avatar-bg-cyan' AS AvatarClass,
            CASE i.inst_type_id
                WHEN 121 THEN 'inquiry/accounts' WHEN 122 THEN 'inquiry/sales'
                ELSE ''
            END AS Route
        FROM digital.instructions i JOIN digital.inst_types t ON i.inst_type_id = t.id
        WHERE i.client_id = @ViewingClientId AND i.inst_type_id = ANY(@InquiryTypeIds) AND i.instruction_id IS NOT NULL
        ORDER BY i.instruction_id, i.datetime DESC;";
                sidebar.InquiryChats.AddRange(await connection.QueryAsync<SidebarChatItem>(inquirySql, new { ViewingClientId = viewingClientId, InquiryTypeIds = inquiryTypeIds }));
            }
            return sidebar;
        }
        public async Task<IEnumerable<ChatMessage>> GetInstructionTicketsForUserAsync(long clientAuthUserId)
        {
            var sql = @"
        SELECT DISTINCT ON (instruction_id)
            id, instruction_id, datetime, instruction, status, inst_type_id
        FROM digital.instructions
        WHERE client_auth_user_id = @ClientAuthUserId AND instruction_id IS NOT NULL
        ORDER BY instruction_id, datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<ChatMessage>(sql, new { ClientAuthUserId = clientAuthUserId });
            }
        }

        public async Task<IEnumerable<ChatMessage>> GetConversationsByInstTypeAsync(short instTypeId, long? clientId = null)
        {
            var sql = @"
        SELECT id, datetime, instruction, assigned_to, status, completed, instruction_id
        FROM digital.instructions
        WHERE inst_type_id = @InstTypeId
          AND (@ClientId IS NULL OR client_id = @ClientId)
        ORDER BY datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<ChatMessage>(sql, new { InstTypeId = instTypeId, ClientId = clientId });
            }
        }

        public async Task<ChatMessage> GetInstructionByIdAsync(long instructionId)
        {
            var sql = @"
        SELECT 
            id, datetime, instruction, assigned_to, status, 
            completed, attachment_id
        FROM digital.instructions
        WHERE id = @InstructionId;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryFirstOrDefaultAsync<ChatMessage>(sql, new { InstructionId = instructionId });
            }
        }
        public async Task<IEnumerable<ChatMessage>> GetMessagesByConversationIdAsync(long conversationId, long? clientId = null)
        {
            var sql = @"
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
            i.inst_channel,
            CASE 
                WHEN i.client_auth_user_id IS NOT NULL THEN COALESCE(cu.full_name, cu.user_name, 'Unknown Client User')
                ELSE COALESCE(au.full_name, au.user_name, 'Unknown Admin User')
            END AS SenderName
        FROM digital.instructions i
        LEFT JOIN admin.users au ON i.insert_user = au.id AND i.client_auth_user_id IS NULL
        LEFT JOIN internal.support_users cu ON i.client_auth_user_id = cu.id
        WHERE i.instruction_id = @ConversationId
          AND (@ClientId IS NULL OR i.client_id = @ClientId)
        ORDER BY i.datetime ASC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<ChatMessage>(sql, new { ConversationId = conversationId, ClientId = clientId });
            }
        }


        public async Task<IEnumerable<TicketViewModel>> GetTicketsByClientIdAsync(long clientId)
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
            i.expiry_date AS ExpiryDate  
        FROM digital.instructions i
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users res ON i.completed_by = res.id
        WHERE i.client_id = @ClientId AND i.inst_category_id = 101
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<TicketViewModel>(sql, new { ClientId = clientId });
            }
        }

        public async Task<IEnumerable<InquiryViewModel>> GetInquiriesByClientIdAsync(long clientId)
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
            END AS Outcome
        FROM digital.instructions i
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN digital.inst_types t ON i.inst_type_id = t.id
        WHERE i.client_id = @ClientId AND i.inst_category_id = 102
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<InquiryViewModel>(sql, new { ClientId = clientId });
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

        public async Task<IEnumerable<TicketViewModel>> GetAllTicketsAsync()
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
            COALESCE(u.full_name, 'Unknown Client') AS ClientName
        FROM digital.instructions i
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users res ON i.completed_by = res.id
        WHERE i.inst_category_id = 101
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<TicketViewModel>(sql, new { });
            }
        }
        public async Task<IEnumerable<InquiryViewModel>> GetAllInquiriesAsync()
        {
            var inquiryTypeIds = new[] { 121, 122 };

            var countSql = @"
        SELECT COUNT(*) 
        FROM digital.instructions i 
        WHERE i.inst_type_id = ANY(@InquiryTypeIds);";

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
            COALESCE(u.full_name, au.full_name, 'Unknown Client') AS ClientName
        FROM digital.instructions i
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users au ON i.insert_user = au.id
        LEFT JOIN digital.inst_types t ON i.inst_type_id = t.id
        WHERE i.inst_type_id = ANY(@InquiryTypeIds)
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                try
                {
                    var count = await connection.ExecuteScalarAsync<int>(countSql, new { InquiryTypeIds = inquiryTypeIds });
                    Console.WriteLine($"DEBUG: Found {count} records with inquiry type IDs 121 or 122");

                    if (count == 0)
                    {
                        var existingTypesSql = "SELECT DISTINCT inst_type_id FROM digital.instructions ORDER BY inst_type_id;";
                        var existingTypes = await connection.QueryAsync<int>(existingTypesSql);
                        Console.WriteLine($"DEBUG: Existing inst_type_id values: {string.Join(", ", existingTypes)}");
                    }

                    var result = await connection.QueryAsync<InquiryViewModel>(sql, new { InquiryTypeIds = inquiryTypeIds });
                    Console.WriteLine($"DEBUG: GetAllInquiriesAsync returned {result.Count()} records");
                    return result;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR in GetAllInquiriesAsync: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                    throw;
                }
            }
        }

        public async Task<DashboardStatsViewModel> GetDashboardStatsAsync()
        {
            var sql = @"
        SELECT 
            COUNT(*) FILTER (WHERE i.inst_category_id = 101) AS TotalTickets,
            COUNT(*) FILTER (WHERE i.inst_category_id = 101 AND i.completed = false) AS OpenTickets,
            COUNT(*) FILTER (WHERE i.inst_category_id = 101 AND i.completed = true) AS ResolvedTickets,
            COUNT(*) FILTER (WHERE i.inst_category_id = 102) AS TotalInquiries
        FROM digital.instructions i;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryFirstOrDefaultAsync<DashboardStatsViewModel>(sql);
            }
        }

        public async Task<bool> UpdateInstructionAsync(ChatMessage instruction)
        {
            var checkSql = @"
        SELECT id, completed, instruction, remarks, expiry_date, edit_date, edit_user
        FROM digital.instructions 
        WHERE id = @Id";

            var updateSql = @"
        UPDATE digital.instructions 
        SET instruction = @Instruction,
            remarks = @Remarks,
            expiry_date = @ExpiryDate,
            edit_date = @EditDate,
            edit_user = @EditUser
        WHERE id = @Id
          AND inst_category_id = 101
          AND (completed = false OR completed IS NULL)";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                try
                {
                    var existingRecord = await connection.QueryFirstOrDefaultAsync(checkSql, new { Id = instruction.Id });

                    Console.WriteLine($"DEBUG: Record check for ID {instruction.Id}:");
                    if (existingRecord == null)
                    {
                        Console.WriteLine($"ERROR: No record found with ID {instruction.Id}");
                        return false;
                    }

                    Console.WriteLine($"DEBUG: Existing record: {System.Text.Json.JsonSerializer.Serialize(existingRecord)}");

                    var parameters = new
                    {
                        Id = instruction.Id,
                        Instruction = instruction.Instruction,
                        Remarks = instruction.Remarks,
                        ExpiryDate = instruction.ExpiryDate,
                        EditDate = instruction.EditDate,
                        EditUser = instruction.EditUser
                    };

                    Console.WriteLine($"DEBUG: Update parameters: {System.Text.Json.JsonSerializer.Serialize(parameters)}");
                    Console.WriteLine($"DEBUG: Update SQL: {updateSql}");

                    var rowsAffected = await connection.ExecuteAsync(updateSql, parameters);

                    Console.WriteLine($"DEBUG: Rows affected: {rowsAffected}");

                    return rowsAffected > 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR in UpdateInstructionAsync: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                    throw;
                }
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
        WHERE i.inst_type_id = ANY(@TicketTypeIds) AND i.completed = true
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
        WHERE i.inst_type_id = ANY(@InquiryTypeIds) AND i.completed = true
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
        WHERE i.inst_type_id = ANY(@TicketTypeIds) AND (i.completed = false OR i.completed IS NULL)
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
        WHERE i.inst_type_id = ANY(@InquiryTypeIds) AND (i.completed = false OR i.completed IS NULL)
        ORDER BY i.datetime DESC;";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QueryAsync<InquiryViewModel>(sql, new { InquiryTypeIds = inquiryTypeIds });
            }
        }

        public async Task<bool> UpdateInquiryStatusAsync(long inquiryId, bool isCompleted, long? completedByUserId = null)
        {
            var sql = @"
        UPDATE digital.instructions 
        SET completed = @IsCompleted,
            completed_by = @CompletedByUserId,
            completed_on = CASE WHEN @IsCompleted = true THEN @CompletedOn ELSE NULL END,
            edit_date = @EditDate,
            edit_user = @EditUser
        WHERE id = @InquiryId AND inst_category_id = 102";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                var rowsAffected = await connection.ExecuteAsync(sql, new
                {
                    InquiryId = inquiryId,
                    IsCompleted = isCompleted,
                    CompletedByUserId = completedByUserId,
                    CompletedOn = isCompleted ? DateTime.UtcNow : (DateTime?)null,
                    EditDate = DateTime.UtcNow,
                    EditUser = completedByUserId
                });

                return rowsAffected > 0;
            }
        }

        public async Task<bool> UpdateTicketStatusAsync(long ticketId, bool isCompleted, long? completedByUserId = null)
        {
            var sql = @"
        UPDATE digital.instructions 
        SET completed = @IsCompleted,
            completed_by = @CompletedByUserId,
            completed_on = CASE WHEN @IsCompleted = true THEN @CompletedOn ELSE NULL END,
            edit_date = @EditDate,
            edit_user = @EditUser
        WHERE id = @TicketId AND inst_category_id = 101";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                var rowsAffected = await connection.ExecuteAsync(sql, new
                {
                    TicketId = ticketId,
                    IsCompleted = isCompleted,
                    CompletedByUserId = completedByUserId,
                    CompletedOn = isCompleted ? DateTime.UtcNow : (DateTime?)null,
                    EditDate = DateTime.UtcNow,
                    EditUser = completedByUserId
                });

                return rowsAffected > 0;
            }
        }

        public async Task<TicketViewModel?> GetTicketDetailsByIdAsync(long ticketId, long? clientId = null)
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
            i.expiry_date AS ExpiryDate,
            i.completed_on AS ResolvedDate,
            COALESCE(u.full_name, 'Unknown Client') AS ClientName,
            i.client_id AS ClientId
        FROM digital.instructions i
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users res ON i.completed_by = res.id
        WHERE i.id = @TicketId
          AND i.inst_category_id = 101
          AND (@ClientId IS NULL OR i.client_id = @ClientId)";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QuerySingleOrDefaultAsync<TicketViewModel>(sql, new { TicketId = ticketId, ClientId = clientId });
            }
        }

        public async Task<InquiryViewModel?> GetInquiryDetailsByIdAsync(long inquiryId, long? clientId = null)
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
            i.completed_on AS ResolvedDate
        FROM digital.instructions i
        LEFT JOIN internal.support_users u ON i.client_auth_user_id = u.id
        LEFT JOIN admin.users au ON i.insert_user = au.id
        LEFT JOIN digital.inst_types t ON i.inst_type_id = t.id
        WHERE i.id = @InquiryId
          AND i.inst_category_id = 102
          AND (@ClientId IS NULL OR i.client_id = @ClientId)";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QuerySingleOrDefaultAsync<InquiryViewModel>(sql, new { InquiryId = inquiryId, ClientId = clientId });
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
        LEFT JOIN internal.support_users ca ON i.insert_user = ca.id
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
    }
}
