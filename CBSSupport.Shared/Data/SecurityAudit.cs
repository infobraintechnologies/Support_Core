using System.Text.Json;
using Dapper;
using Npgsql;

namespace CBSSupport.Shared.Data;

public static class SecurityAuditActorKinds
{
    public const string Anonymous = "Anonymous";
    public const string Admin = "Admin";
    public const string Client = "Client";
    public const string System = "System";
}

public static class SecurityAuditOutcomes
{
    public const string Success = "Success";
    public const string Failure = "Failure";
    public const string Denied = "Denied";
    public const string Throttled = "Throttled";
    public const string Error = "Error";
}

public sealed record SecurityAuditEvent(
    long? TenantId,
    string ActorKind,
    long? ActorUserId,
    string? TargetKind,
    string? TargetId,
    string Action,
    string Outcome,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    string? IpPrefix,
    IReadOnlyDictionary<string, string?>? SourceContext = null,
    IReadOnlyDictionary<string, string?>? Details = null)
{
    public void Validate()
    {
        if (TenantId is <= 0 || ActorUserId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TenantId));
        }

        if (ActorKind is not (SecurityAuditActorKinds.Anonymous
            or SecurityAuditActorKinds.Admin
            or SecurityAuditActorKinds.Client
            or SecurityAuditActorKinds.System))
        {
            throw new ArgumentException("Unknown audit actor kind.", nameof(ActorKind));
        }

        if (ActorKind is SecurityAuditActorKinds.Anonymous or SecurityAuditActorKinds.System
            && ActorUserId is not null)
        {
            throw new ArgumentException("Anonymous and system events cannot have an actor user ID.", nameof(ActorUserId));
        }

        if (ActorKind is SecurityAuditActorKinds.Admin or SecurityAuditActorKinds.Client
            && ActorUserId is null)
        {
            throw new ArgumentException("Authenticated events require an actor user ID.", nameof(ActorUserId));
        }

        ValidateText(Action, nameof(Action), 64, required: true);
        ValidateText(TargetKind, nameof(TargetKind), 32, required: false);
        ValidateText(TargetId, nameof(TargetId), 128, required: false);
        ValidateText(CorrelationId, nameof(CorrelationId), 128, required: false);
        ValidateText(IpPrefix, nameof(IpPrefix), 64, required: false);

        if (Outcome is not (SecurityAuditOutcomes.Success
            or SecurityAuditOutcomes.Failure
            or SecurityAuditOutcomes.Denied
            or SecurityAuditOutcomes.Throttled
            or SecurityAuditOutcomes.Error))
        {
            throw new ArgumentException("Unknown audit outcome.", nameof(Outcome));
        }

        ValidateMetadata(SourceContext, nameof(SourceContext));
        ValidateMetadata(Details, nameof(Details));
    }

    private static void ValidateText(string? value, string parameterName, int maxLength, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Audit text is required.", parameterName);
        }

        if (value is not null && (value.Length > maxLength || value.Any(char.IsControl)))
        {
            throw new ArgumentException("Audit text is invalid.", parameterName);
        }
    }

    private static void ValidateMetadata(
        IReadOnlyDictionary<string, string?>? metadata,
        string parameterName)
    {
        if (metadata is null)
        {
            return;
        }

        foreach (var pair in metadata)
        {
            ValidateText(pair.Key, parameterName, 48, required: true);
            ValidateText(pair.Value, parameterName, 256, required: false);
            if (pair.Key.Contains("password", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Contains("token", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Contains("body", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Contains("exception", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Sensitive metadata is not permitted in audit events.", parameterName);
            }
        }
    }
}

public sealed record SecurityAuditRecord(
    long AuditId,
    long? TenantId,
    string ActorKind,
    long? ActorUserId,
    string? TargetKind,
    string? TargetId,
    string Action,
    string Outcome,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    string? IpPrefix,
    IReadOnlyDictionary<string, string?> SourceContext,
    IReadOnlyDictionary<string, string?> Details);

public interface ISecurityAuditWriter
{
    Task AppendAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default);

    Task AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SecurityAuditEvent auditEvent,
        CancellationToken cancellationToken = default);
}

public interface ISecurityAuditReader
{
    Task<IReadOnlyList<SecurityAuditRecord>> ListAsync(
        long? tenantId,
        DateTimeOffset? from,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class SecurityAuditWriter(string connectionString)
    : ISecurityAuditWriter, ISecurityAuditReader
{
    public async Task AppendAsync(
        SecurityAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await AppendAsync(connection, transaction: null, auditEvent, cancellationToken);
    }

    public async Task AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SecurityAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        auditEvent.Validate();

        await connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            Parameters(auditEvent),
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<SecurityAuditRecord>> ListAsync(
        long? tenantId,
        DateTimeOffset? from,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (tenantId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId));
        }

        limit = Math.Clamp(limit, 1, 100);
        await using var connection = new NpgsqlConnection(connectionString);
        var rows = await connection.QueryAsync<SecurityAuditRow>(new CommandDefinition(
            """
            SELECT audit_id AS AuditId,
                   tenant_id AS TenantId,
                   actor_kind AS ActorKind,
                   actor_user_id AS ActorUserId,
                   target_kind AS TargetKind,
                   target_id AS TargetId,
                   action AS Action,
                   outcome AS Outcome,
                   occurred_at AS OccurredAt,
                   correlation_id AS CorrelationId,
                   ip_prefix AS IpPrefix,
                   source_context::text AS SourceContext,
                   details::text AS Details
            FROM digital.security_audit_events
            WHERE (@TenantId IS NULL OR tenant_id = @TenantId)
              AND (@From IS NULL OR occurred_at >= @From)
            ORDER BY occurred_at DESC, audit_id DESC
            LIMIT @Limit;
            """,
            new { TenantId = tenantId, From = from, Limit = limit },
            cancellationToken: cancellationToken));

        return rows.Select(static row => row.ToRecord()).ToArray();
    }

    private static DynamicParameters Parameters(SecurityAuditEvent auditEvent)
    {
        var parameters = new DynamicParameters();
        parameters.Add("TenantId", auditEvent.TenantId);
        parameters.Add("ActorKind", auditEvent.ActorKind);
        parameters.Add("ActorUserId", auditEvent.ActorUserId);
        parameters.Add("TargetKind", auditEvent.TargetKind);
        parameters.Add("TargetId", auditEvent.TargetId);
        parameters.Add("Action", auditEvent.Action);
        parameters.Add("Outcome", auditEvent.Outcome);
        parameters.Add("OccurredAt", auditEvent.OccurredAt);
        parameters.Add("CorrelationId", auditEvent.CorrelationId);
        parameters.Add("IpPrefix", auditEvent.IpPrefix);
        parameters.Add("SourceContext", Serialize(auditEvent.SourceContext));
        parameters.Add("Details", Serialize(auditEvent.Details));
        return parameters;
    }

    private static string Serialize(IReadOnlyDictionary<string, string?>? metadata) =>
        JsonSerializer.Serialize(metadata ?? new Dictionary<string, string?>());

    private const string InsertSql = """
        INSERT INTO digital.security_audit_events (
            tenant_id, actor_kind, actor_user_id, target_kind, target_id,
            action, outcome, occurred_at, correlation_id, ip_prefix,
            source_context, details)
        VALUES (
            @TenantId, @ActorKind, @ActorUserId, @TargetKind, @TargetId,
            @Action, @Outcome, @OccurredAt, @CorrelationId, @IpPrefix,
            CAST(@SourceContext AS jsonb), CAST(@Details AS jsonb));
        """;

    private sealed class SecurityAuditRow
    {
        public long AuditId { get; set; }
        public long? TenantId { get; set; }
        public string ActorKind { get; set; } = string.Empty;
        public long? ActorUserId { get; set; }
        public string? TargetKind { get; set; }
        public string? TargetId { get; set; }
        public string Action { get; set; } = string.Empty;
        public string Outcome { get; set; } = string.Empty;
        public DateTimeOffset OccurredAt { get; set; }
        public string? CorrelationId { get; set; }
        public string? IpPrefix { get; set; }
        public string SourceContext { get; set; } = "{}";
        public string Details { get; set; } = "{}";

        public SecurityAuditRecord ToRecord() => new(
            AuditId,
            TenantId,
            ActorKind,
            ActorUserId,
            TargetKind,
            TargetId,
            Action,
            Outcome,
            OccurredAt,
            CorrelationId,
            IpPrefix,
            Deserialize(SourceContext),
            Deserialize(Details));

        private static IReadOnlyDictionary<string, string?> Deserialize(string json) =>
            JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
            ?? new Dictionary<string, string?>();
    }
}

public sealed class NullSecurityAuditWriter : ISecurityAuditWriter, ISecurityAuditReader
{
    public Task AppendAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SecurityAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<SecurityAuditRecord>> ListAsync(
        long? tenantId,
        DateTimeOffset? from,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SecurityAuditRecord>>([]);
}
