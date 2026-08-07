using CBSSupport.Shared.Data;
using Microsoft.AspNetCore.Http;

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
    ILogger<AccountSecurityStampRotationService> logger,
    ISecurityAuditWriter? securityAudit = null,
    IHttpContextAccessor? httpContextAccessor = null) : IAccountSecurityStampRotationService
{
    private readonly ISecurityAuditWriter _securityAudit = securityAudit ?? new NullSecurityAuditWriter();

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
        var auditEvent = CreateAuditEvent(account, reason);
        var rotated = store is ITransactionalAccountSecurityStampStore transactionalStore
            ? await transactionalStore.RotateWithAuditAsync(
                account,
                replacementStamp,
                expectedStamp,
                auditEvent,
                cancellationToken)
            : await store.RotateAsync(account, replacementStamp, expectedStamp, cancellationToken);
        if (store is not ITransactionalAccountSecurityStampStore || !rotated)
        {
            await _securityAudit.AppendAsync(
                auditEvent with
                {
                    Outcome = rotated ? SecurityAuditOutcomes.Success : SecurityAuditOutcomes.Failure
                },
                cancellationToken);
        }
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

    private SecurityAuditEvent CreateAuditEvent(
        AccountReference account,
        SecurityStampRotationReason reason)
    {
        var context = httpContextAccessor?.HttpContext;
        var currentActor = context is null
            ? new SecurityAuditActor(SecurityAuditActorKinds.System, null, null)
            : SecurityAuditContext.FromPrincipal(context.User);
        if (currentActor.ActorKind == SecurityAuditActorKinds.Anonymous)
        {
            currentActor = new SecurityAuditActor(SecurityAuditActorKinds.System, null, null);
        }

        return new SecurityAuditEvent(
            account.Kind == AccountKind.Client ? currentActor.TenantId : null,
            currentActor.ActorKind,
            currentActor.ActorUserId,
            "Account",
            account.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reason switch
            {
                SecurityStampRotationReason.RevokeAllSessions => "RevokeAll",
                SecurityStampRotationReason.PasswordChange => "PasswordChanged",
                SecurityStampRotationReason.PasswordReset => "PasswordReset",
                SecurityStampRotationReason.RoleChange => "RoleChanged",
                SecurityStampRotationReason.AccountCompromise => "AccountCompromise",
                _ => "SecurityStampChanged"
            },
            SecurityAuditOutcomes.Success,
            DateTimeOffset.UtcNow,
            context is null
                ? null
                : System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier,
            context is null ? null : SecurityAuditContext.MaskIp(context.Connection.RemoteIpAddress),
            new Dictionary<string, string?> { ["feature"] = "identity" },
            new Dictionary<string, string?> { ["reason"] = reason.ToString() });
    }
}
