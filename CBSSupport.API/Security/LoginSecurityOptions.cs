namespace CBSSupport.API.Security;

public sealed class LoginSecurityOptions
{
    public const string SectionName = "Security:LoginAttempts";

    public int SourcePermitLimit { get; set; } = 20;
    public TimeSpan SourceWindow { get; set; } = TimeSpan.FromMinutes(1);
    public int FailedAttemptsBeforeBackoff { get; set; } = 5;
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaximumBackoff { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan StateRetention { get; set; } = TimeSpan.FromHours(1);
    public int CleanupBatchSize { get; set; } = 1_000;
    public int CleanupEveryOperations { get; set; } = 256;
    public TimeSpan StoreFailureRetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (SourcePermitLimit <= 0
            || SourceWindow <= TimeSpan.Zero
            || FailedAttemptsBeforeBackoff <= 0
            || InitialBackoff <= TimeSpan.Zero
            || MaximumBackoff < InitialBackoff
            || StateRetention < MaximumBackoff
            || StateRetention < SourceWindow
            || CleanupBatchSize <= 0
            || CleanupEveryOperations <= 0
            || StoreFailureRetryAfter <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Configuration section '{SectionName}' contains invalid login throttling values.");
        }
    }
}
