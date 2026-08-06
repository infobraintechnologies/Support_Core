namespace CBSSupport.Shared.Services;

public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";
    public bool Enabled { get; set; }
    public AttachmentSecurityMode SecurityMode { get; set; } =
        AttachmentSecurityMode.StructuralValidationOnly;
    public long DefaultTenantQuotaBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public long MaximumFileBytes { get; set; } = 10L * 1024 * 1024;
    public int MaximumFilesPerMessage { get; set; } = 5;
    public long MaximumBytesPerMessage { get; set; } = 25L * 1024 * 1024;
    public int MaximumConcurrentUnboundPerUser { get; set; } = 10;
    public long MaximumUserBytesPerRollingDay { get; set; } = 100L * 1024 * 1024;
    public int UploadUrlLifetimeSeconds { get; set; } = 300;
    public int DownloadUrlLifetimeSeconds { get; set; } = 60;
    public int ReadyUnboundHours { get; set; } = 24;
    public int PendingUploadHours { get; set; } = 1;
    public int BoundRetentionDays { get; set; } = 365;
    public AttachmentScanningOptions Scanning { get; set; } = new();
    public AttachmentStructuralValidationOptions StructuralValidation { get; set; } = new();
    public R2StorageOptions R2 { get; set; } = new();

    public void Validate()
    {
        const long oneGiB = 1024L * 1024 * 1024;
        const long tenMiB = 10L * 1024 * 1024;
        if (DefaultTenantQuotaBytes < oneGiB
            || MaximumFileBytes != tenMiB
            || MaximumFilesPerMessage != 5
            || MaximumBytesPerMessage != 25L * 1024 * 1024
            || MaximumConcurrentUnboundPerUser != 10
            || MaximumUserBytesPerRollingDay != 100L * 1024 * 1024
            || UploadUrlLifetimeSeconds != 300
            || DownloadUrlLifetimeSeconds != 60
            || ReadyUnboundHours != 24
            || PendingUploadHours != 1
            || BoundRetentionDays != 365)
        {
            throw new InvalidOperationException(
                "Attachment limits and retention must match the approved security profile.");
        }
        if (!Enum.IsDefined(SecurityMode))
        {
            throw new InvalidOperationException("Unknown attachment security mode.");
        }
        if (SecurityMode == AttachmentSecurityMode.MalwareScanning
            && (Scanning.MaxConcurrentScans is < 1 or > 4
            || Scanning.TimeoutSeconds <= 0
            || Scanning.MaximumAttempts <= 0
            || Scanning.MinimumBackoffSeconds != 15
            || Scanning.MaximumBackoffSeconds != 120
            || Scanning.MinimumBackoffSeconds > Scanning.MaximumBackoffSeconds
            || Scanning.MaximumDefinitionAgeHours != 24
            || Scanning.HealthCheckSeconds != 60))
        {
            throw new InvalidOperationException(
                "Attachment scanner settings are outside the approved operating profile.");
        }
        if (StructuralValidation.MaxConcurrentValidations is < 1 or > 4
            || StructuralValidation.MaximumAttempts <= 0
            || StructuralValidation.MinimumBackoffSeconds != 15
            || StructuralValidation.MaximumBackoffSeconds != 120
            || StructuralValidation.MinimumBackoffSeconds
                > StructuralValidation.MaximumBackoffSeconds
            || StructuralValidation.MaximumImageWidth is < 1 or > 20_000
            || StructuralValidation.MaximumImageHeight is < 1 or > 20_000
            || StructuralValidation.MaximumImagePixels is < 1 or > 100_000_000
            || StructuralValidation.MaximumDecodedImageBytes is < 1
            || StructuralValidation.MaximumPackageEntries is < 1
            || StructuralValidation.MaximumPackageUncompressedBytes is < 1
            || StructuralValidation.MaximumPackageCompressionRatio is < 1
            || StructuralValidation.MaximumPdfObjects is < 1
            || StructuralValidation.MaximumPdfPages is < 1
            || StructuralValidation.MaximumPdfDecodedBytes is < 1)
        {
            throw new InvalidOperationException(
                "Attachment structural validation settings are outside the approved operating profile.");
        }

        if (Enabled)
        {
            R2.Validate();
        }
    }
}

public enum AttachmentSecurityMode
{
    StructuralValidationOnly,
    MalwareScanning
}

public sealed class AttachmentStructuralValidationOptions
{
    public int MaxConcurrentValidations { get; set; } = 4;
    public int MaximumAttempts { get; set; } = 3;
    public int MinimumBackoffSeconds { get; set; } = 15;
    public int MaximumBackoffSeconds { get; set; } = 120;
    public int MaximumImageWidth { get; set; } = 10_000;
    public int MaximumImageHeight { get; set; } = 10_000;
    public long MaximumImagePixels { get; set; } = 40_000_000;
    public long MaximumDecodedImageBytes { get; set; } = 160L * 1024 * 1024;
    public int MaximumPackageEntries { get; set; } = 1_024;
    public long MaximumPackageUncompressedBytes { get; set; } = 64L * 1024 * 1024;
    public int MaximumPackageCompressionRatio { get; set; } = 100;
    public int MaximumPdfObjects { get; set; } = 20_000;
    public int MaximumPdfPages { get; set; } = 500;
    public long MaximumPdfDecodedBytes { get; set; } = 64L * 1024 * 1024;
}

public sealed class AttachmentScanningOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3310;
    public bool WorkerEnabled { get; set; }
    public int MaxConcurrentScans { get; set; } = 4;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaximumAttempts { get; set; } = 3;
    public int MinimumBackoffSeconds { get; set; } = 15;
    public int MaximumBackoffSeconds { get; set; } = 120;
    public int MaximumDefinitionAgeHours { get; set; } = 24;
    public int HealthCheckSeconds { get; set; } = 60;
}

public sealed class R2StorageOptions
{
    public string AccountId { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string BucketName { get; set; } = "";
    public string? ServiceUrl { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccessKeyId)
            || string.IsNullOrWhiteSpace(SecretAccessKey)
            || string.IsNullOrWhiteSpace(BucketName)
            || (string.IsNullOrWhiteSpace(ServiceUrl)
                && string.IsNullOrWhiteSpace(AccountId)))
        {
            throw new InvalidOperationException(
                "Attachments:R2 requires AccessKeyId, SecretAccessKey, BucketName, and AccountId or ServiceUrl when attachments are enabled.");
        }
    }
}
