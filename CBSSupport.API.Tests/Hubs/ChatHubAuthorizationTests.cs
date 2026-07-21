using System.Reflection;
using System.Security.Claims;
using CBSSupport.API.Hubs;
using CBSSupport.API.Security;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Hubs;

public sealed class ChatHubAuthorizationTests
{
    private const long UserId = 7;
    private const long ClientId = 42;
    private const long ConversationId = 123;

    [Fact]
    public void ChatHub_UsesAdminOrClientPolicy()
    {
        var attribute = Assert.Single(
            typeof(ChatHub).GetCustomAttributes<AuthorizeAttribute>());

        Assert.Equal(Policies.AdminOrClient, attribute.Policy);
    }

    [Fact]
    public void ChatHub_PublicClientMethods_MatchSecureContract()
    {
        var methodNames = typeof(ChatHub)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.Name != nameof(ChatHub.OnConnectedAsync))
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(ChatHub.JoinConversation),
                nameof(ChatHub.LeaveConversation),
                nameof(ChatHub.SendMessage),
                nameof(ChatHub.SetTyping)
            }.OrderBy(name => name, StringComparer.Ordinal),
            methodNames);

        var forbiddenParameterNames = new HashSet<string>(
            ["senderName", "userName", "clientId", "groupName"],
            StringComparer.OrdinalIgnoreCase);

        Assert.All(
            typeof(ChatHub).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            method => Assert.DoesNotContain(
                method.GetParameters(),
                parameter => forbiddenParameterNames.Contains(parameter.Name ?? string.Empty)));
    }

    [Theory]
    [InlineData("NotifyTicketStatusUpdate")]
    [InlineData("NotifyInquiryStatusUpdate")]
    [InlineData("NotifyTicketCreated")]
    [InlineData("NotifyInquiryCreated")]
    [InlineData("JoinPrivateChat")]
    [InlineData("SendAdminMessage")]
    [InlineData("SendClientMessage")]
    [InlineData("SendPublicMessage")]
    public void ChatHub_PrivilegedOrIdentitySpoofingMethod_IsNotPublic(string methodName)
    {
        Assert.DoesNotContain(
            typeof(ChatHub).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            method => method.Name == methodName);
    }

    [Fact]
    public void SendConversationMessageRequest_ExposesOnlyCallerControlledMessageFields()
    {
        var propertyNames = typeof(SendConversationMessageRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "AttachmentIds", "Text" },
            propertyNames);
    }

    [Fact]
    public async Task JoinConversation_ClientOwnConversation_AddsCanonicalGroup()
    {
        var fixture = CreateFixture(CreateClientPrincipal());
        fixture.Service.AccessResolver = (conversationId, actor) =>
            conversationId == ConversationId && actor.ClientId == ClientId
                ? CreateAccess(conversationId, ClientId)
                : null;

        await fixture.Hub.JoinConversation(ConversationId);

        var accessCall = Assert.Single(fixture.Service.AccessCalls);
        Assert.Equal(ConversationId, accessCall.ConversationId);
        Assert.Equal(UserId, accessCall.Actor.UserId);
        Assert.Equal(ClientId, accessCall.Actor.ClientId);
        Assert.False(accessCall.Actor.IsAdmin);

        var groupCall = Assert.Single(fixture.Groups.AddCalls);
        Assert.Equal("connection-1", groupCall.ConnectionId);
        Assert.Equal("conversation:123", groupCall.GroupName);
    }

    [Fact]
    public async Task JoinConversation_ClientOtherTenant_ThrowsAndDoesNotJoin()
    {
        var fixture = CreateFixture(CreateClientPrincipal());
        fixture.Service.AccessResolver = (_, _) => null;

        var exception = await Assert.ThrowsAsync<HubException>(
            () => fixture.Hub.JoinConversation(ConversationId));

        Assert.Equal("Conversation unavailable.", exception.Message);
        Assert.Single(fixture.Service.AccessCalls);
        Assert.Empty(fixture.Groups.AddCalls);
    }

    [Fact]
    public async Task JoinConversation_AdminWithAccess_UsesActorWithoutTenantAndJoins()
    {
        var fixture = CreateFixture(CreateAdminPrincipal());
        fixture.Service.AccessResolver = (conversationId, actor) =>
            conversationId == ConversationId && actor.IsAdmin && actor.ClientId is null
                ? CreateAccess(conversationId, ClientId)
                : null;

        await fixture.Hub.JoinConversation(ConversationId);

        var accessCall = Assert.Single(fixture.Service.AccessCalls);
        Assert.Equal(UserId, accessCall.Actor.UserId);
        Assert.Null(accessCall.Actor.ClientId);
        Assert.True(accessCall.Actor.IsAdmin);
        Assert.Equal("admin-user", accessCall.Actor.DisplayName);
        var groupCall = Assert.Single(fixture.Groups.AddCalls);
        Assert.Equal("connection-1", groupCall.ConnectionId);
        Assert.Equal("conversation:123", groupCall.GroupName);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("conflicting")]
    public async Task JoinConversation_ClientWithInvalidTenantIdentity_RejectsBeforeServiceOrGroup(
        string claimScenario)
    {
        var fixture = CreateFixture(CreateClientPrincipalWithTenantClaims(claimScenario));

        var exception = await Assert.ThrowsAsync<HubException>(
            () => fixture.Hub.JoinConversation(ConversationId));

        Assert.Equal("Authenticated tenant identity is unavailable.", exception.Message);
        Assert.Empty(fixture.Service.AccessCalls);
        Assert.Empty(fixture.Groups.AddCalls);
    }

    [Fact]
    public async Task SetTyping_Authorized_UsesClaimIdentityAndExcludesCallerConnection()
    {
        var fixture = CreateFixture(CreateClientPrincipal("trusted-user"));
        fixture.Service.AccessResolver = (conversationId, _) =>
            CreateAccess(conversationId, ClientId);

        await fixture.Hub.SetTyping(ConversationId, true);

        var selection = Assert.Single(fixture.Clients.GroupExceptSelections);
        Assert.Equal("conversation:123", selection.GroupName);
        Assert.Equal(["connection-1"], selection.ExcludedConnectionIds);

        var send = Assert.Single(fixture.Clients.Proxy.Calls);
        Assert.Equal("TypingChanged", send.Method);
        var payload = Assert.Single(send.Arguments);
        Assert.Equal(ConversationId, ReadProperty<long>(payload, "ConversationId"));
        Assert.Equal(UserId, ReadProperty<long>(payload, "UserId"));
        Assert.Equal("trusted-user", ReadProperty<string>(payload, "DisplayName"));
        Assert.True(ReadProperty<bool>(payload, "IsTyping"));
    }

    [Fact]
    public async Task SetTyping_ClientOtherTenant_DoesNotBroadcast()
    {
        var fixture = CreateFixture(CreateClientPrincipal());
        fixture.Service.AccessResolver = (_, _) => null;

        var exception = await Assert.ThrowsAsync<HubException>(
            () => fixture.Hub.SetTyping(ConversationId, true));

        Assert.Equal("Conversation unavailable.", exception.Message);
        Assert.Single(fixture.Service.AccessCalls);
        Assert.Empty(fixture.Clients.GroupExceptSelections);
        Assert.Empty(fixture.Clients.Proxy.Calls);
    }

    [Fact]
    public async Task LeaveConversation_ClientOtherTenant_DoesNotRemoveGroupMembership()
    {
        var fixture = CreateFixture(CreateClientPrincipal());
        fixture.Service.AccessResolver = (_, _) => null;

        var exception = await Assert.ThrowsAsync<HubException>(
            () => fixture.Hub.LeaveConversation(ConversationId));

        Assert.Equal("Conversation unavailable.", exception.Message);
        Assert.Single(fixture.Service.AccessCalls);
        Assert.Empty(fixture.Groups.RemoveCalls);
    }

    [Fact]
    public async Task SendMessage_Authorized_PersistsWithClaimActorAndBroadcastsTrustedResponse()
    {
        var fixture = CreateFixture(CreateClientPrincipal("trusted-user"));
        fixture.Service.AccessResolver = (conversationId, _) =>
            CreateAccess(conversationId, ClientId);
        var trustedMessage = new ConversationMessage(
            501,
            ConversationId,
            "Persisted text",
            new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
            new ConversationSender(UserId, "trusted-user", Roles.Client),
            null);
        fixture.Service.CreateResult = trustedMessage;

        var result = await fixture.Hub.SendMessage(
            ConversationId,
            new SendConversationMessageRequest("Caller text"));

        Assert.Same(trustedMessage, result);
        var createCall = Assert.Single(fixture.Service.CreateCalls);
        Assert.Equal(ConversationId, createCall.ConversationId);
        Assert.Equal("Caller text", createCall.Text);
        Assert.Null(createCall.IpAddress);
        Assert.Equal(UserId, createCall.Actor.UserId);
        Assert.Equal(ClientId, createCall.Actor.ClientId);
        Assert.False(createCall.Actor.IsAdmin);
        Assert.Equal("trusted-user", createCall.Actor.DisplayName);

        var selection = Assert.Single(fixture.Clients.GroupExceptSelections);
        Assert.Equal("conversation:123", selection.GroupName);
        Assert.Equal(["connection-1"], selection.ExcludedConnectionIds);
        var send = Assert.Single(fixture.Clients.Proxy.Calls);
        Assert.Equal("MessageCreated", send.Method);
        Assert.Same(trustedMessage, Assert.Single(send.Arguments));
    }

    [Fact]
    public async Task SendMessage_ClientOtherTenant_DoesNotPersistOrBroadcast()
    {
        var fixture = CreateFixture(CreateClientPrincipal());
        fixture.Service.AccessResolver = (_, _) => null;

        var exception = await Assert.ThrowsAsync<HubException>(
            () => fixture.Hub.SendMessage(
                ConversationId,
                new SendConversationMessageRequest("Do not send")));

        Assert.Equal("Conversation unavailable.", exception.Message);
        Assert.Single(fixture.Service.AccessCalls);
        Assert.Empty(fixture.Service.CreateCalls);
        Assert.Empty(fixture.Clients.GroupExceptSelections);
        Assert.Empty(fixture.Clients.Proxy.Calls);
    }

    private static HubFixture CreateFixture(ClaimsPrincipal principal)
    {
        var service = new RecordingConversationService();
        var groups = new RecordingGroupManager();
        var clients = new RecordingHubCallerClients();
        var hub = new ChatHub(service, NullLogger<ChatHub>.Instance)
        {
            Context = new TestHubCallerContext("connection-1", principal),
            Groups = groups,
            Clients = clients
        };

        return new HubFixture(hub, service, groups, clients);
    }

    private static ClaimsPrincipal CreateClientPrincipal(string displayName = "client-user")
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                new Claim(ClaimTypes.Name, displayName),
                new Claim(ClaimTypes.Role, Roles.Client),
                new Claim(CustomClaimTypes.ClientId, ClientId.ToString())
            ],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreateAdminPrincipal()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                new Claim(ClaimTypes.Name, "admin-user"),
                new Claim(ClaimTypes.Role, Roles.Admin)
            ],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreateClientPrincipalWithTenantClaims(string claimScenario)
    {
        var tenantClaims = claimScenario switch
        {
            "missing" => Array.Empty<Claim>(),
            "invalid" => [new Claim(CustomClaimTypes.ClientId, "0")],
            "conflicting" =>
            [
                new Claim(CustomClaimTypes.ClientId, ClientId.ToString()),
                new Claim(CustomClaimTypes.LegacyClientId, "99")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(claimScenario))
        };

        var identity = new ClaimsIdentity(
            tenantClaims.Concat(
            [
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                new Claim(ClaimTypes.Name, "client-user"),
                new Claim(ClaimTypes.Role, Roles.Client)
            ]),
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private static ConversationAccess CreateAccess(long conversationId, long clientId) =>
        new(conversationId, clientId, 110, 101);

    private static T ReadProperty<T>(object? value, string propertyName)
    {
        Assert.NotNull(value);
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(value));
    }

    private sealed record HubFixture(
        ChatHub Hub,
        RecordingConversationService Service,
        RecordingGroupManager Groups,
        RecordingHubCallerClients Clients);

    private sealed record AccessCall(long ConversationId, ConversationActor Actor);

    private sealed record CreateCall(
        long ConversationId,
        ConversationActor Actor,
        string Text,
        string? IpAddress);

    private sealed class RecordingConversationService : IConversationService
    {
        public Func<long, ConversationActor, ConversationAccess?> AccessResolver { get; set; } =
            (_, _) => null;

        public ConversationMessage? CreateResult { get; set; }

        public List<AccessCall> AccessCalls { get; } = [];

        public List<CreateCall> CreateCalls { get; } = [];

        public Task<ConversationAccess?> GetAccessAsync(
            long conversationId,
            ConversationActor actor,
            CancellationToken cancellationToken = default)
        {
            AccessCalls.Add(new AccessCall(conversationId, actor));
            return Task.FromResult(AccessResolver(conversationId, actor));
        }

        public Task<ConversationMessage?> CreateMessageAsync(
            long conversationId,
            ConversationActor actor,
            string text,
            string? ipAddress,
            CancellationToken cancellationToken = default)
        {
            CreateCalls.Add(new CreateCall(conversationId, actor, text, ipAddress));
            return Task.FromResult(CreateResult);
        }
    }

    private sealed record GroupCall(string ConnectionId, string GroupName);

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<GroupCall> AddCalls { get; } = [];

        public List<GroupCall> RemoveCalls { get; } = [];

        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            AddCalls.Add(new GroupCall(connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            RemoveCalls.Add(new GroupCall(connectionId, groupName));
            return Task.CompletedTask;
        }
    }

    private sealed record ClientSend(string Method, IReadOnlyList<object?> Arguments);

    private sealed class RecordingClientProxy : ISingleClientProxy
    {
        public List<ClientSend> Calls { get; } = [];

        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ClientSend(method, args));
            return Task.CompletedTask;
        }

        public Task<T> InvokeCoreAsync<T>(
            string method,
            object?[] args,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed record GroupExceptSelection(
        string GroupName,
        IReadOnlyList<string> ExcludedConnectionIds);

    private sealed class RecordingHubCallerClients : IHubCallerClients
    {
        public RecordingClientProxy Proxy { get; } = new();

        public List<GroupExceptSelection> GroupExceptSelections { get; } = [];

        public IClientProxy All => Proxy;

        public IClientProxy Others => Proxy;

        public IClientProxy Caller => Proxy;

        public IClientProxy OthersInGroup(string groupName) => Proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IClientProxy Client(string connectionId) => Proxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;

        public IClientProxy Group(string groupName) => Proxy;

        public IClientProxy GroupExcept(
            string groupName,
            IReadOnlyList<string> excludedConnectionIds)
        {
            GroupExceptSelections.Add(
                new GroupExceptSelection(groupName, excludedConnectionIds.ToArray()));
            return Proxy;
        }

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;

        public IClientProxy User(string userId) => Proxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];
        private readonly IFeatureCollection _features = new FeatureCollection();
        private readonly ClaimsPrincipal _user;

        public TestHubCallerContext(string connectionId, ClaimsPrincipal user)
        {
            ConnectionId = connectionId;
            _user = user;
        }

        public override string ConnectionId { get; }

        public override string? UserIdentifier =>
            _user.FindFirstValue(ClaimTypes.NameIdentifier);

        public override ClaimsPrincipal User => _user;

        public override IDictionary<object, object?> Items => _items;

        public override IFeatureCollection Features => _features;

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }
}
