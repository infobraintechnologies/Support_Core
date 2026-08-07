using System.Security.Claims;
using CBSSupport.API.Security;
using CBSSupport.Shared.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Security;

public sealed class SecurityAuditTests
{
    [Fact]
    public void SecurityAuditEvent_RejectsSensitiveMetadata()
    {
        var audit = CreateEvent(details: new Dictionary<string, string?>
        {
            ["authorizationHeader"] = "should never be recorded"
        });

        Assert.Throws<ArgumentException>(audit.Validate);
    }

    [Fact]
    public void SecurityAuditContext_MasksNetworkAndUsesClaimTenant()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Role, Roles.Client),
            new Claim(CustomClaimTypes.ClientId, "7")
        ], "test"));
        var context = new DefaultHttpContext
        {
            User = principal
        };
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.23");

        var audit = SecurityAuditContext.ForHttpRequest(
            context,
            "AuthenticationSucceeded",
            SecurityAuditOutcomes.Success,
            tenantId: 999);

        Assert.Equal(7, audit.TenantId);
        Assert.Equal(SecurityAuditActorKinds.Client, audit.ActorKind);
        Assert.Equal(42, audit.ActorUserId);
        Assert.Equal("198.51.100.0/24", audit.IpPrefix);
    }

    [Fact]
    public async Task PasswordChange_UsesTransactionalAuditWithClaimActor()
    {
        var store = new RecordingTransactionalStore();
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "9"),
                    new Claim(ClaimTypes.Role, Roles.Admin)
                ], "test"))
            }
        };
        var service = new AccountSecurityStampRotationService(
            store,
            new FakeStampService(),
            new LocalHubConnectionRevocationNotifier(new ActiveHubConnectionRegistry()),
            NullLogger<AccountSecurityStampRotationService>.Instance,
            new RecordingAuditWriter(),
            httpContextAccessor);

        var rotated = await service.RotateForPasswordChangeAsync(
            new AccountReference(AccountKind.Client, 11));

        Assert.True(rotated);
        Assert.NotNull(store.AuditEvent);
        Assert.Equal("PasswordChanged", store.AuditEvent!.Action);
        Assert.Equal(SecurityAuditActorKinds.Admin, store.AuditEvent.ActorKind);
        Assert.Equal(9, store.AuditEvent.ActorUserId);
        Assert.Equal("Account", store.AuditEvent.TargetKind);
        Assert.Equal("11", store.AuditEvent.TargetId);
    }

    [Fact]
    public async Task FailedRotation_EmitsFailureWithoutChangingProtectedState()
    {
        var store = new RecordingTransactionalStore { Result = false };
        var auditWriter = new RecordingAuditWriter();
        var service = new AccountSecurityStampRotationService(
            store,
            new FakeStampService(),
            new LocalHubConnectionRevocationNotifier(new ActiveHubConnectionRegistry()),
            NullLogger<AccountSecurityStampRotationService>.Instance,
            auditWriter);

        var rotated = await service.RevokeAllSessionsAsync(
            new AccountReference(AccountKind.Administrator, 7));

        Assert.False(rotated);
        Assert.Null(store.AuditEvent);
        Assert.Equal(SecurityAuditOutcomes.Failure, auditWriter.Events.Single().Outcome);
        Assert.Equal("RevokeAll", auditWriter.Events.Single().Action);
    }

    [Fact]
    public void SecurityAuditMigration_RevokesMutationPrivilegesAndInstallsAppendOnlyTrigger()
    {
        var root = FindRepositoryRoot();
        var sql = File.ReadAllText(Path.Combine(
            root,
            "Database",
            "Migrations",
            "202608071000_create_security_audit_events.sql"));

        Assert.Contains("BEFORE UPDATE OR DELETE", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER", sql, StringComparison.Ordinal);
        Assert.Contains("cbs_support_audit_owner", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON TABLE digital.security_audit_events FROM PUBLIC", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT UPDATE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static SecurityAuditEvent CreateEvent(
        IReadOnlyDictionary<string, string?>? details = null) => new(
        7,
        SecurityAuditActorKinds.Client,
        42,
        "Account",
        "42",
        "AuthenticationSucceeded",
        SecurityAuditOutcomes.Success,
        DateTimeOffset.UtcNow,
        "trace-1",
        "198.51.100.0/24",
        new Dictionary<string, string?> { ["transport"] = "http" },
        details);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CBSSupportSolution.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class FakeStampService : IAccountSecurityStampService
    {
        public byte[] Generate() => Enumerable.Repeat((byte)4, 32).ToArray();

        public bool Matches(string protectedStamp, byte[] expectedStamp) => true;

        public string Create(byte[] stamp) => Convert.ToBase64String(stamp);
    }

    private sealed class RecordingAuditWriter : ISecurityAuditWriter
    {
        public List<SecurityAuditEvent> Events { get; } = [];

        public Task AppendAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task AppendAsync(
            Npgsql.NpgsqlConnection connection,
            Npgsql.NpgsqlTransaction? transaction,
            SecurityAuditEvent auditEvent,
            CancellationToken cancellationToken = default) =>
            AppendAsync(auditEvent, cancellationToken);
    }

    private sealed class RecordingTransactionalStore :
        IAccountSecurityStampStore,
        ITransactionalAccountSecurityStampStore
    {
        public bool Result { get; init; } = true;
        public SecurityAuditEvent? AuditEvent { get; private set; }

        public Task<bool> RotateAsync(
            AccountReference account,
            byte[] replacementStamp,
            byte[]? expectedStamp = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);

        public Task<bool> RotateWithAuditAsync(
            AccountReference account,
            byte[] replacementStamp,
            byte[]? expectedStamp,
            SecurityAuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            if (Result)
            {
                AuditEvent = auditEvent;
            }
            return Task.FromResult(Result);
        }
    }
}
