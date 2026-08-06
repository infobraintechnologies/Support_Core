using CBSSupport.Shared.Services;

namespace CBSSupport.API.Tests.Attachments;

public sealed class AttachmentOptionsTests
{
    [Fact]
    public void Validate_ApprovedDefaults_SucceedsWithFeatureDisabled()
    {
        var options = new AttachmentOptions();

        options.Validate();

        Assert.False(options.Enabled);
        Assert.Equal(AttachmentSecurityMode.StructuralValidationOnly, options.SecurityMode);
        Assert.False(options.Scanning.WorkerEnabled);
    }

    [Fact]
    public void Validate_TenantQuotaBelowOneGiB_Throws()
    {
        var options = new AttachmentOptions
        {
            DefaultTenantQuotaBytes = 1024 * 1024
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_EnabledWithoutR2Credentials_Throws()
    {
        var options = new AttachmentOptions { Enabled = true };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_EnabledWithR2ServiceUrlAndCredentials_Succeeds()
    {
        var options = new AttachmentOptions
        {
            Enabled = true,
            R2 = new R2StorageOptions
            {
                AccessKeyId = "test-access-key",
                SecretAccessKey = "test-secret-key",
                BucketName = "test-bucket",
                ServiceUrl = "http://127.0.0.1:1"
            }
        };

        options.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Validate_ScanConcurrencyOutsideOneToFour_IsIgnoredInStructuralMode(int concurrency)
    {
        var options = new AttachmentOptions();
        options.Scanning.MaxConcurrentScans = concurrency;

        options.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Validate_ScanConcurrencyOutsideOneToFour_ThrowsInMalwareScanningMode(int concurrency)
    {
        var options = new AttachmentOptions { SecurityMode = AttachmentSecurityMode.MalwareScanning };
        options.Scanning.MaxConcurrentScans = concurrency;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
