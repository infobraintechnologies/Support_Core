namespace CBSSupport.API.Security;

public sealed class HubConnectionRevocationMonitor(
    IActiveHubConnectionRegistry connections,
    IAccountPrincipalValidator principalValidator,
    TimeProvider timeProvider,
    ILogger<HubConnectionRevocationMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromSeconds(30);
    public const int MaxConcurrentValidations = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RevalidationInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RevalidateConnectionsAsync(stoppingToken);
        }
    }

    public async Task RevalidateConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await Parallel.ForEachAsync(
            connections.GetConnections(),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxConcurrentValidations
            },
            async (connection, itemCancellationToken) =>
            {
                if (connection.Context.ConnectionAborted.IsCancellationRequested)
                {
                    connections.Remove(connection.ConnectionId);
                    return;
                }

                try
                {
                    if (await principalValidator.ValidateAsync(
                        connection.Context.User,
                        itemCancellationToken))
                    {
                        return;
                    }

                    logger.LogWarning(
                        "Aborting SignalR connection {ConnectionId} for revoked user {UserId}",
                        connection.ConnectionId,
                        GetUserId(connection.Context.User));
                    connection.Context.Abort();
                    connections.Remove(connection.ConnectionId);
                }
                catch (OperationCanceledException) when (itemCancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    logger.LogWarning(
                        "SignalR revocation check failed for connection {ConnectionId}; the connection will be retried",
                        connection.ConnectionId);
                }
            });
    }

    private static long? GetUserId(System.Security.Claims.ClaimsPrincipal? principal) =>
        principal is not null && principal.TryGetUserId(out var userId)
            ? userId
            : null;
}
