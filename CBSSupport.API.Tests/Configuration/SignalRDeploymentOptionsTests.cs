using CBSSupport.API.Configuration;
namespace CBSSupport.API.Tests.Configuration;

public sealed class SignalRDeploymentOptionsTests
{
    [Fact]
    public void DefaultMode_IsSingleInstance()
    {
        var options = new SignalRDeploymentOptions();

        Assert.Equal("SingleInstance", options.DeploymentMode);
        options.Validate();
    }

    [Fact]
    public void UnsupportedMode_IsRejected()
    {
        var options = new SignalRDeploymentOptions
        {
            DeploymentMode = "MultiInstance"
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => options.Validate());

        Assert.Contains("SingleInstance", exception.Message, StringComparison.Ordinal);
    }
}
