using CBSSupport.Shared.Data;

namespace CBSSupport.API.Security;

public interface IHubConnectionRevocationNotifier
{
    Task NotifyAsync(
        AccountReference account,
        CancellationToken cancellationToken = default);
}

public sealed class LocalHubConnectionRevocationNotifier(
    IActiveHubConnectionRegistry connections) : IHubConnectionRevocationNotifier
{
    public Task NotifyAsync(
        AccountReference account,
        CancellationToken cancellationToken = default)
    {
        connections.AbortAccount(account);
        return Task.CompletedTask;
    }
}
