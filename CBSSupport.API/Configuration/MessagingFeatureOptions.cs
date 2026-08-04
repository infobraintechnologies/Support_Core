namespace CBSSupport.API.Configuration;

public sealed class MessagingFeatureOptions
{
    public const string SectionName = "Messaging:Features";

    public bool GroupEnabled { get; set; } = true;

    public bool PrivateEnabled { get; set; }
}
