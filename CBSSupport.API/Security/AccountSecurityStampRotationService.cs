using CBSSupport.Shared.Data;

namespace CBSSupport.API.Security;

public enum SecurityStampRotationReason
{
    RevokeAllSessions,
    PasswordChange,
    PasswordReset,
    RoleChange,
    AccountCompromise
}

public interface IAccountSecurityStampRotationService
{
    Task<bool> RotateAsync(
        AccountReference account,
        SecurityStampRotationReason reason,
        byte[]? expectedStamp = null,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeAllSessionsAsync(
        AccountReference account,
        CancellationToken cancellationToken = default);

    Task<bool> RotateForPasswordChangeAsync(
        AccountReference account,
        byte[]? expectedStamp = null,
        CancellationToken cancellationToken = default);

    Task<bool> RotateForPasswordResetAsync(
        AccountReference account,
        CancellationToken cancellationToken = default);

    Task<bool> RotateForRoleChangeAsync(
        AccountReference account,
        CancellationToken cancellationToken = default);

    Task<bool> RotateForAccountCompromiseAsync(
        AccountReference account,
        CancellationToken cancellationToken = default);
}

public sealed class AccountSecurityStampRotationService(
    IAccountSecurityStampStore store,
    IAccountSecurityStampService stamps,
    IHubConnectionRevocationNotifier revocations,
    ILogger<AccountSecurityStampRotationService> logger) : IAccountSecurityStampRotationService
{
    public Task<bool> RevokeAllSessionsAsync(
        AccountReference account,
        CancellationToken cancellationToken = default) =>
        RotateAsync(account, SecurityStampRotationReason.RevokeAllSessions, cancellationToken: cancellationToken);

    public Task<bool> RotateForPasswordChangeAsync(
        AccountReference account,
        byte[]? expectedStamp = null,
        CancellationToken cancellationToken = default) =>
        RotateAsync(
            account,
            SecurityStampRotationReason.PasswordChange,
            expectedStamp,
            cancellationToken);

    public Task<bool> RotateForPasswordResetAsync(
        AccountReference account,
        CancellationToken cancellationToken = default) =>
        RotateAsync(account, SecurityStampRotationReason.PasswordReset, cancellationToken: cancellationToken);

    public Task<bool> RotateForRoleChangeAsync(
        AccountReference account,
        CancellationToken cancellationToken = default) =>
        RotateAsync(account, SecurityStampRotationReason.RoleChange, cancellationToken: cancellationToken);

    public Task<bool> RotateForAccountCompromiseAsync(
        AccountReference account,
        CancellationToken cancellationToken = default) =>
        RotateAsync(account, SecurityStampRotationReason.AccountCompromise, cancellationToken: cancellationToken);

    public async Task<bool> RotateAsync(
        AccountReference account,
        SecurityStampRotationReason reason,
        byte[]? expectedStamp = null,
        CancellationToken cancellationToken = default)
    {
        var replacementStamp = stamps.Generate();
        var rotated = await store.RotateAsync(
            account,
            replacementStamp,
            expectedStamp,
            cancellationToken);
        if (rotated)
        {
            try
            {
                await revocations.NotifyAsync(account, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Security stamp rotated for {AccountKind} user {UserId}, but SignalR revocation fan-out was unavailable",
                    account.Kind,
                    account.UserId);
            }

            logger.LogInformation(
                "Security stamp rotated for {AccountKind} user {UserId} because of {Reason}",
                account.Kind,
                account.UserId,
                reason);
        }

        return rotated;
    }
}
