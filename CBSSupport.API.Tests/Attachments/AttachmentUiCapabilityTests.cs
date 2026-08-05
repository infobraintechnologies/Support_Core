using CBSSupport.API.Attachments;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Tests.Attachments;

public sealed class AttachmentUiCapabilityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StructuralValidationOnly_EnabledWithoutScanner_AllowsUploadUi()
    {
        var capability = Create(
            new AttachmentOptions
            {
                Enabled = true,
                SecurityMode = AttachmentSecurityMode.StructuralValidationOnly
            },
            scanner: null);

        Assert.True(capability.CanCreateUploadIntents);
    }

    [Fact]
    public void AttachmentsDisabled_HidesUploadUi()
    {
        var capability = Create(
            new AttachmentOptions
            {
                Enabled = false,
                SecurityMode = AttachmentSecurityMode.StructuralValidationOnly
            },
            scanner: null);

        Assert.False(capability.CanCreateUploadIntents);
    }

    [Fact]
    public void MalwareScanning_UnhealthyOrStaleScanner_HidesUploadUi()
    {
        var unhealthy = Create(
            MalwareOptions(),
            new StubScanner(new(false, Now, null, "scanner_unavailable")));
        var stale = Create(
            MalwareOptions(),
            new StubScanner(new(true, Now.AddMinutes(-3), Now, null)));

        Assert.False(unhealthy.CanCreateUploadIntents);
        Assert.False(stale.CanCreateUploadIntents);
    }

    [Fact]
    public void MalwareScanning_HealthyFreshScanner_AllowsUploadUi()
    {
        var capability = Create(
            MalwareOptions(),
            new StubScanner(new(true, Now, Now, null)));

        Assert.True(capability.CanCreateUploadIntents);
    }

    private static AttachmentOptions MalwareOptions() =>
        new()
        {
            Enabled = true,
            SecurityMode = AttachmentSecurityMode.MalwareScanning
        };

    private static AttachmentUiCapability Create(
        AttachmentOptions options,
        IFileScanner? scanner) =>
        new(options, scanner, new FixedTimeProvider(Now));

    private sealed class StubScanner(FileScannerHealth health) : IFileScanner
    {
        public FileScannerHealth Health { get; } = health;

        public Task<FileScannerHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Health);

        public Task<FileScanResult> ScanAsync(
            Stream content,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
