using System.Security.Claims;
using CBSSupport.API.Security;
using CBSSupport.Shared.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace CBSSupport.API.Tests.Security;

public sealed class ActiveHubConnectionRegistryTests
{
    [Fact]
    public void AbortAccount_AbortsOnlyConnectionsForTheRequestedAccount()
    {
        var registry = new ActiveHubConnectionRegistry();
        var matching = new TestHubCallerContext("matching", CreatePrincipal(7, Roles.Client));
        var otherUser = new TestHubCallerContext("other-user", CreatePrincipal(8, Roles.Client));
        var admin = new TestHubCallerContext("admin", CreatePrincipal(7, Roles.Admin));
        registry.Register(matching);
        registry.Register(otherUser);
        registry.Register(admin);

        registry.AbortAccount(new AccountReference(AccountKind.Client, 7));

        Assert.True(matching.WasAborted);
        Assert.False(otherUser.WasAborted);
        Assert.False(admin.WasAborted);
        Assert.Equal(
            ["admin", "other-user"],
            registry.GetConnections().Select(connection => connection.ConnectionId).Order());
    }

    private static ClaimsPrincipal CreatePrincipal(long userId, string role) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role)
            ],
            "Test"));

    private sealed class TestHubCallerContext(
        string connectionId,
        ClaimsPrincipal user) : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];
        private readonly IFeatureCollection _features = new FeatureCollection();

        public bool WasAborted { get; private set; }

        public override string ConnectionId => connectionId;

        public override string? UserIdentifier => user.FindFirstValue(ClaimTypes.NameIdentifier);

        public override ClaimsPrincipal User => user;

        public override IDictionary<object, object?> Items => _items;

        public override IFeatureCollection Features => _features;

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() => WasAborted = true;
    }
}
