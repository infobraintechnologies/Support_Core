using Dapper;
using Npgsql;

namespace CBSSupport.Shared.Data;

public enum AccountKind
{
    Administrator,
    Client
}

public readonly record struct AccountReference(AccountKind Kind, long UserId);

public interface IAccountSecurityStampStore
{
    Task<bool> RotateAsync(
        AccountReference account,
        byte[] replacementStamp,
        byte[]? expectedStamp = null,
        CancellationToken cancellationToken = default);
}

public sealed class AccountSecurityStampStore(string connectionString) : IAccountSecurityStampStore
{
    public async Task<bool> RotateAsync(
        AccountReference account,
        byte[] replacementStamp,
        byte[]? expectedStamp = null,
        CancellationToken cancellationToken = default)
    {
        if (account.UserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(account));
        }

        if (replacementStamp.Length != 32)
        {
            throw new ArgumentException(
                "Security stamps must contain exactly 32 random bytes.",
                nameof(replacementStamp));
        }

        if (expectedStamp is not null && expectedStamp.Length != 32)
        {
            throw new ArgumentException(
                "Expected security stamps must contain exactly 32 bytes.",
                nameof(expectedStamp));
        }

        var sql = account.Kind switch
        {
            AccountKind.Administrator => """
                UPDATE admin.users
                SET security_stamp = @ReplacementStamp
                WHERE id = @UserId
                  AND (@ExpectedStamp IS NULL OR security_stamp = @ExpectedStamp)
                RETURNING id;
                """,
            AccountKind.Client => """
                UPDATE internal.support_users
                SET security_stamp = @ReplacementStamp
                WHERE id = @UserId
                  AND (@ExpectedStamp IS NULL OR security_stamp = @ExpectedStamp)
                RETURNING id;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(account))
        };

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = new CommandDefinition(
            sql,
            new
            {
                UserId = account.UserId,
                ReplacementStamp = replacementStamp,
                ExpectedStamp = expectedStamp
            },
            cancellationToken: cancellationToken);
        var updatedId = await connection.QuerySingleOrDefaultAsync<long?>(command);
        return updatedId is not null;
    }
}
