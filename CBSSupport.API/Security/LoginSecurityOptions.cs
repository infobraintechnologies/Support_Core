namespace CBSSupport.API.Security;

public sealed class LoginSecurityOptions
{
    public const string SectionName = "Security:LoginAttempts";

    public int PerIpPermitLimit { get; set; } = 20;
    public TimeSpan PerIpWindow { get; set; } = TimeSpan.FromMinutes(1);
    public int PerIpSegments { get; set; } = 6;
    public int FailedAttemptsBeforeBackoff { get; set; } = 5;
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaximumBackoff { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan StateRetention { get; set; } = TimeSpan.FromHours(1);
    public int MaximumTrackedAccounts { get; set; } = 100_000;

    public void Validate()
    {
        if (PerIpPermitLimit <= 0
            || PerIpWindow <= TimeSpan.Zero
            || PerIpSegments <= 0
            || FailedAttemptsBeforeBackoff <= 0
            || InitialBackoff <= TimeSpan.Zero
            || MaximumBackoff < InitialBackoff
            || StateRetention < MaximumBackoff
            || MaximumTrackedAccounts <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration section '{SectionName}' contains invalid login throttling values.");
        }
    }
}
