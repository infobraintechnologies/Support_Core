namespace CBSSupport.API.Configuration;

public sealed class SignalRDeploymentOptions
{
    public const string SectionName = "SignalR";

    public string DeploymentMode { get; set; } = "SingleInstance";

    public void Validate()
    {
        if (!DeploymentMode.Equals("SingleInstance", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SignalR:DeploymentMode must be SingleInstance. Multi-instance SignalR is not supported by the current deployment.");
        }
    }
}
