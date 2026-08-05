using System.Diagnostics.Metrics;

namespace CBSSupport.Shared.Services;

internal static class AttachmentMetrics
{
    internal const string MeterName = "CBSSupport.Attachments";
    internal const string TenantQuotaWarningCounterName =
        "cbs_support_attachment_tenant_quota_warnings";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> TenantQuotaWarnings =
        Meter.CreateCounter<long>(
            TenantQuotaWarningCounterName,
            description: "Attachment intents accepted at or above 80% of tenant active-storage quota.");

    internal static void RecordTenantQuotaWarning(
        long clientId,
        long usedBytes,
        long limitBytes) =>
        TenantQuotaWarnings.Add(
            1,
            new KeyValuePair<string, object?>("client.id", clientId),
            new KeyValuePair<string, object?>("quota.used.bytes", usedBytes),
            new KeyValuePair<string, object?>("quota.limit.bytes", limitBytes));
}
