using CBSSupport.Shared.Models;
using Dapper;
using Npgsql;
using System.Threading.Tasks;

namespace CBSSupport.Shared.Data
{
    public class UserRepository : IUserRepository
    {
        private readonly string _connectionString;

        public UserRepository(string connectionString)
        {
            _connectionString = connectionString;
            DefaultTypeMap.MatchNamesWithUnderscores = true;
        }

        public async Task<AdminUser?> GetByUsernameAsync(string username)
        {
            const string sql = """
                SELECT
                    id,
                    user_name,
                    password_salt,
                    password_hash,
                    security_stamp,
                    role_id,
                    full_name,
                    status,
                    deactive_date
                FROM admin.users
                WHERE user_name = @Username
                  AND status IS TRUE
                  AND deactive_date IS NULL
                """;

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QuerySingleOrDefaultAsync<AdminUser>(sql, new { Username = username });
            }
        }

        public async Task<ClientUser?> GetClientUserAsync(long clientId, string username)
        {
            const string sql = """
                SELECT
                    id,
                    client_id,
                    user_name,
                    full_name,
                    password_hash,
                    password_salt,
                    security_stamp,
                    status,
                    deactive_date
                FROM internal.support_users
                WHERE client_id = @ClientId
                  AND user_name = @Username
                  AND status IS TRUE
                  AND deactive_date IS NULL
                """;

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                return await connection.QuerySingleOrDefaultAsync<ClientUser>(
                    sql,
                    new { ClientId = clientId, Username = username }
                );
            }
        }

        public async Task<AdminUser?> GetByIdAsync(
            long userId,
            CancellationToken cancellationToken = default)
        {
            const string sql = """
                SELECT
                    id,
                    user_name,
                    password_salt,
                    password_hash,
                    security_stamp,
                    role_id,
                    full_name,
                    status,
                    deactive_date
                FROM admin.users
                WHERE id = @UserId
                """;

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                var command = new CommandDefinition(
                    sql,
                    new { UserId = userId },
                    cancellationToken: cancellationToken);
                return await connection.QuerySingleOrDefaultAsync<AdminUser>(command);
            }
        }

        public async Task<ClientUser?> GetClientUserByIdAsync(
            long clientId,
            long userId,
            CancellationToken cancellationToken = default)
        {
            const string sql = """
                SELECT
                    id,
                    client_id,
                    user_name,
                    full_name,
                    password_hash,
                    password_salt,
                    security_stamp,
                    status,
                    deactive_date
                FROM internal.support_users
                WHERE id = @UserId
                  AND client_id = @ClientId
                """;

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                var command = new CommandDefinition(
                    sql,
                    new { UserId = userId, ClientId = clientId },
                    cancellationToken: cancellationToken);
                return await connection.QuerySingleOrDefaultAsync<ClientUser>(command);
            }
        }
    }
}
