using Microsoft.AspNetCore.SignalR;

namespace CBSSupport.API.Security;

public sealed class HubPrincipalValidationFilter(
    IAccountPrincipalValidator principalValidator,
    IActiveHubConnectionRegistry connections,
    TimeProvider timeProvider,
    ILogger<HubPrincipalValidationFilter> logger) : IHubFilter
{
    public const string LastValidatedUtcItem = "account:last-validated-utc";
    public static readonly TimeSpan ValidationInterval = TimeSpan.FromMinutes(1);

    public async Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        await EnsureValidAsync(context.Context, forceValidation: true);
        connections.Register(context.Context);
        try
        {
            await next(context);
        }
        catch
        {
            connections.Remove(context.Context.ConnectionId);
            throw;
        }
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        try
        {
            await next(context, exception);
        }
        finally
        {
            connections.Remove(context.Context.ConnectionId);
        }
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        await EnsureValidAsync(invocationContext.Context, forceValidation: false);
        return await next(invocationContext);
    }

    private async Task EnsureValidAsync(
        HubCallerContext context,
        bool forceValidation)
    {
        var now = timeProvider.GetUtcNow();
        if (!forceValidation
            && context.Items.TryGetValue(LastValidatedUtcItem, out var value)
            && value is DateTimeOffset lastValidated
            && lastValidated <= now
            && now - lastValidated < ValidationInterval)
        {
            return;
        }

        if (await principalValidator.ValidateAsync(context.User, context.ConnectionAborted))
        {
            context.Items[LastValidatedUtcItem] = now;
            return;
        }

        var userId = context.User is not null
            && context.User.TryGetUserId(out var parsedUserId)
            ? parsedUserId
            : (long?)null;
        logger.LogWarning(
            "SignalR connection {ConnectionId} aborted after account revocation check for user {UserId}",
            context.ConnectionId,
            userId);
        context.Abort();
        throw new HubException("Authentication session is no longer valid.");
    }
}
