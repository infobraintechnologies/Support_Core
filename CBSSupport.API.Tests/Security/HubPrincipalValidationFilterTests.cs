using System.Reflection;
using System.Security.Claims;
using CBSSupport.API.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Security;

public sealed class HubPrincipalValidationFilterTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InvokeMethod_RevokedAfterBoundedInterval_AbortsBeforeHubMethodRuns()
    {
        var validator = new MutablePrincipalValidator { IsValid = true };
        var timeProvider = new MutableTimeProvider(InitialTime);
        var connections = new ActiveHubConnectionRegistry();
        var filter = new HubPrincipalValidationFilter(
            validator,
            connections,
            timeProvider,
            NullLogger<HubPrincipalValidationFilter>.Instance);
        var callerContext = new TestHubCallerContext(CreatePrincipal());
        var invocationContext = CreateInvocationContext(callerContext);
        var nextCalls = 0;

        await filter.InvokeMethodAsync(invocationContext, _ =>
        {
            nextCalls++;
            return ValueTask.FromResult<object?>(null);
        });

        validator.IsValid = false;
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await filter.InvokeMethodAsync(invocationContext, _ =>
        {
            nextCalls++;
            return ValueTask.FromResult<object?>(null);
        });

        timeProvider.Advance(TimeSpan.FromSeconds(31));
        var exception = await Assert.ThrowsAsync<HubException>(async () =>
            await filter.InvokeMethodAsync(invocationContext, _ =>
            {
                nextCalls++;
                return ValueTask.FromResult<object?>(null);
            }));

        Assert.Equal("Authentication session is no longer valid.", exception.Message);
        Assert.True(callerContext.WasAborted);
        Assert.Equal(2, nextCalls);
        Assert.Equal(2, validator.ValidationCalls);
    }

    [Fact]
    public async Task OnConnected_RevokedPrincipal_AbortsWithoutJoiningHub()
    {
        var validator = new MutablePrincipalValidator { IsValid = false };
        var connections = new ActiveHubConnectionRegistry();
        var filter = new HubPrincipalValidationFilter(
            validator,
            connections,
            new MutableTimeProvider(InitialTime),
            NullLogger<HubPrincipalValidationFilter>.Instance);
        var callerContext = new TestHubCallerContext(CreatePrincipal());
        var hub = new TestHub();
        using var services = new ServiceCollection().BuildServiceProvider();
        var lifetimeContext = new HubLifetimeContext(callerContext, services, hub);
        var nextCalled = false;

        await Assert.ThrowsAsync<HubException>(() =>
            filter.OnConnectedAsync(lifetimeContext, _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }));

        Assert.True(callerContext.WasAborted);
        Assert.False(nextCalled);
        Assert.Empty(connections.GetConnections());
    }

    [Fact]
    public async Task ReconnectAfterStampRotation_RejectsStalePrincipalBeforeJoiningHub()
    {
        var validator = new MutablePrincipalValidator { IsValid = true };
        var connections = new ActiveHubConnectionRegistry();
        var filter = new HubPrincipalValidationFilter(
            validator,
            connections,
            new MutableTimeProvider(InitialTime),
            NullLogger<HubPrincipalValidationFilter>.Instance);
        using var services = new ServiceCollection().BuildServiceProvider();
        var hub = new TestHub();
        var firstContext = new TestHubCallerContext(CreatePrincipal());
        await filter.OnConnectedAsync(
            new HubLifetimeContext(firstContext, services, hub),
            _ => Task.CompletedTask);

        validator.IsValid = false;
        await filter.OnDisconnectedAsync(
            new HubLifetimeContext(firstContext, services, hub),
            exception: null,
            (_, _) => Task.CompletedTask);

        var reconnectContext = new TestHubCallerContext(CreatePrincipal());
        await Assert.ThrowsAsync<HubException>(() =>
            filter.OnConnectedAsync(
                new HubLifetimeContext(reconnectContext, services, hub),
                _ => Task.CompletedTask));

        Assert.True(reconnectContext.WasAborted);
        Assert.Empty(connections.GetConnections());
    }

    [Fact]
    public async Task ConnectionLifecycle_RegistersAfterValidation_AndRemovesOnDisconnect()
    {
        var validator = new MutablePrincipalValidator { IsValid = true };
        var connections = new ActiveHubConnectionRegistry();
        var filter = new HubPrincipalValidationFilter(
            validator,
            connections,
            new MutableTimeProvider(InitialTime),
            NullLogger<HubPrincipalValidationFilter>.Instance);
        var callerContext = new TestHubCallerContext(CreatePrincipal());
        var hub = new TestHub();
        using var services = new ServiceCollection().BuildServiceProvider();
        var lifetimeContext = new HubLifetimeContext(callerContext, services, hub);

        await filter.OnConnectedAsync(lifetimeContext, _ => Task.CompletedTask);

        var connection = Assert.Single(connections.GetConnections());
        Assert.Equal(callerContext.ConnectionId, connection.ConnectionId);

        await filter.OnDisconnectedAsync(
            lifetimeContext,
            exception: null,
            (_, _) => Task.CompletedTask);

        Assert.Empty(connections.GetConnections());
    }

    [Fact]
    public async Task OnConnected_DownstreamFailure_RemovesRegistration()
    {
        var validator = new MutablePrincipalValidator { IsValid = true };
        var connections = new ActiveHubConnectionRegistry();
        var filter = new HubPrincipalValidationFilter(
            validator,
            connections,
            new MutableTimeProvider(InitialTime),
            NullLogger<HubPrincipalValidationFilter>.Instance);
        var callerContext = new TestHubCallerContext(CreatePrincipal());
        var hub = new TestHub();
        using var services = new ServiceCollection().BuildServiceProvider();
        var lifetimeContext = new HubLifetimeContext(callerContext, services, hub);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            filter.OnConnectedAsync(
                lifetimeContext,
                _ => throw new InvalidOperationException("Connection setup failed.")));

        Assert.Empty(connections.GetConnections());
    }

    private static HubInvocationContext CreateInvocationContext(HubCallerContext callerContext)
    {
        var hub = new TestHub();
        var method = typeof(TestHub).GetMethod(nameof(TestHub.Ping), BindingFlags.Instance | BindingFlags.Public)!;
        var services = new ServiceCollection().BuildServiceProvider();
        return new HubInvocationContext(callerContext, services, hub, method, []);
    }

    private static ClaimsPrincipal CreatePrincipal() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "7")],
            "Test"));

    private sealed class TestHub : Hub
    {
        public Task Ping() => Task.CompletedTask;
    }

    private sealed class MutablePrincipalValidator : IAccountPrincipalValidator
    {
        public bool IsValid { get; set; }

        public int ValidationCalls { get; private set; }

        public Task<bool> ValidateAsync(
            ClaimsPrincipal? principal,
            CancellationToken cancellationToken = default)
        {
            ValidationCalls++;
            return Task.FromResult(IsValid);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class TestHubCallerContext(ClaimsPrincipal user) : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];
        private readonly IFeatureCollection _features = new FeatureCollection();

        public bool WasAborted { get; private set; }

        public override string ConnectionId => "connection-1";

        public override string? UserIdentifier => user.FindFirstValue(ClaimTypes.NameIdentifier);

        public override ClaimsPrincipal User => user;

        public override IDictionary<object, object?> Items => _items;

        public override IFeatureCollection Features => _features;

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() => WasAborted = true;
    }
}
