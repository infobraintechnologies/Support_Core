using System.Diagnostics.Metrics;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Tests.Attachments;

public sealed class AttachmentMetricsTests
{
    [Fact]
    public void TenantQuotaWarning_RecordsTenantAndByteContext()
    {
        long measurement = 0;
        Dictionary<string, object?> tags = [];
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == AttachmentMetrics.MeterName
                    && instrument.Name == AttachmentMetrics.TenantQuotaWarningCounterName)
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, tagList, state) =>
            {
                measurement += value;
                tags = tagList.ToArray().ToDictionary(item => item.Key, item => item.Value);
            });
        listener.Start();

        AttachmentMetrics.RecordTenantQuotaWarning(
            clientId: 42,
            usedBytes: 4_500,
            limitBytes: 5_000);

        Assert.Equal(1, measurement);
        Assert.Equal(42L, tags["client.id"]);
        Assert.Equal(4_500L, tags["quota.used.bytes"]);
        Assert.Equal(5_000L, tags["quota.limit.bytes"]);
    }
}
