namespace CBSSupport.API.Realtime;

public sealed class ConversationOutboxDispatcherOptions
{
    public int BatchSize { get; set; } = 50;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxAttempts { get; set; } = 8;
}
