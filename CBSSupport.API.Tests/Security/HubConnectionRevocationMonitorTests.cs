using System.Security.Claims;
using CBSSupport.API.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Security;

public sealed class HubConnectionRevocationMonitorTests
{
    [Fact]
    public void RevalidationInterval_IsThirtySeconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            HubConnectionRevocationMonitor.RevalidationInterval);
    }

    [Fact]
    public async Task RevalidateConnections_RevokedIdleConnection_IsAbortedAndRemoved()
    {
        var connections = new ActiveHubConnectionRegistry();
        var context = new TestHubCallerContext(CreatePrincipal());
        connections.Register(context);
        var validator = new StubPrincipalValidator(_ => Task.FromResult(false));
        var monitor = CreateMonitor(connections, validator);

        await monitor.RevalidateConnectionsAsync();

        Assert.True(context.WasAborted);
        Assert.Empty(connections.GetConnections());
        Assert.Equal(1, validator.ValidationCalls);
    }

    [Fact]
    public async Task RevalidateConnections_TransientFailure_KeepsConnectionForRetry()
    {
        var connections = new ActiveHubConnectionRegistry();
        var context = new TestHubCallerContext(CreatePrincipal());
        connections.Register(context);
        var attempts = 0;
        var validator = new StubPrincipalValidator(_ =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<bool>(new InvalidOperationException("Database unavailable."))
                : Task.FromResult(false);
        });
        var monitor = CreateMonitor(connections, validator);

        await monitor.RevalidateConnectionsAsync();

        Assert.False(context.WasAborted);
        Assert.Single(connections.GetConnections());

        await monitor.RevalidateConnectionsAsync();

        Assert.True(context.WasAborted);
        Assert.Empty(connections.GetConnections());
    }

    [Fact]
    public async Task RevalidateConnections_AlreadyDisconnected_RemovesWithoutValidation()
    {
        var connections = new ActiveHubConnectionRegistry();
        using var disconnected = new CancellationTokenSource();
        var context = new TestHubCallerContext(CreatePrincipal(), disconnected.Token);
        connections.Register(context);
        disconnected.Cancel();
        var validator = new StubPrincipalValidator(_ => Task.FromResult(true));
        var monitor = CreateMonitor(connections, validator);

        await monitor.RevalidateConnectionsAsync();

        Assert.Empty(connections.GetConnections());
        Assert.Equal(0, validator.ValidationCalls);
    }

    private static HubConnectionRevocationMonitor CreateMonitor(
        IActiveHubConnectionRegistry connections,
        IAccountPrincipalValidator validator) =>
        new(
            connections,
            validator,
            TimeProvider.System,
            NullLogger<HubConnectionRevocationMonitor>.Instance);

    private static ClaimsPrincipal CreatePrincipal() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "7")],
            "Test"));

    private sealed class StubPrincipalValidator(
        Func<CancellationToken, Task<bool>> validate) : IAccountPrincipalValidator
    {
        public int ValidationCalls { get; private set; }

        public Task<bool> ValidateAsync(
            ClaimsPrincipal? principal,
            CancellationToken cancellationToken = default)
        {
            ValidationCalls++;
            return validate(cancellationToken);
        }
    }

    private sealed class TestHubCallerContext(
        ClaimsPrincipal user,
        CancellationToken connectionAborted = default) : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];
        private readonly IFeatureCollection _features = new FeatureCollection();

        public bool WasAborted { get; private set; }

        public override string ConnectionId => "connection-1";

        public override string? UserIdentifier => user.FindFirstValue(ClaimTypes.NameIdentifier);

        public override ClaimsPrincipal User => user;

        public override IDictionary<object, object?> Items => _items;

        public override IFeatureCollection Features => _features;

        public override CancellationToken ConnectionAborted => connectionAborted;

        public override void Abort() => WasAborted = true;
    }
}
