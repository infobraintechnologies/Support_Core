using System.Reflection;
using System.Security.Claims;
using CBSSupport.API.Hubs;
using CBSSupport.API.Realtime;
using CBSSupport.API.Security;
using CBSSupport.API.Configuration;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CBSSupport.API.Tests.Hubs;

public sealed class ChatHubAuthorizationTests
{
    private const int UserId = 7;
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
    public async Task OnConnected_Client_JoinsTrustedTenantAudience()
    {
        var fixture = CreateFixture(CreateClientPrincipal());

        await fixture.Hub.OnConnectedAsync();

        var groupCall = Assert.Single(fixture.Groups.AddCalls);
        Assert.Equal("connection-1", groupCall.ConnectionId);
        Assert.Equal(RealtimeGroupNames.Tenant(ClientId), groupCall.GroupName);
    }

    [Fact]
    public async Task OnConnected_Admin_JoinsTrustedAdminAudience()
    {
        var fixture = CreateFixture(CreateAdminPrincipal());

        await fixture.Hub.OnConnectedAsync();

        var groupCall = Assert.Single(fixture.Groups.AddCalls);
        Assert.Equal("connection-1", groupCall.ConnectionId);
        Assert.Equal(RealtimeGroupNames.Admins, groupCall.GroupName);
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
    public async Task SetTyping_GroupConversation_UsesClaimIdentityAndExcludesCallerConnection()
    {
        var fixture = CreateFixture(CreateClientPrincipal("trusted-user"));
        fixture.Service.AccessResolver = (conversationId, _) =>
            new ConversationAccess(
                conversationId,
                ClientId,
                ConversationTypes.SupportGroup,
                100,
                ClientUserId: UserId);

        await fixture.Hub.SetTyping(ConversationId, true);

        var selection = Assert.Single(fixture.Clients.GroupExceptSelections);
        Assert.Equal("conversation:123", selection.GroupName);
        Assert.Equal(["connection-1"], selection.ExcludedConnectionIds);

        var payload = Assert.Single(fixture.Clients.Proxy.TypingCalls);
        Assert.Equal(ConversationId, payload.ConversationId);
        Assert.Equal(UserId, payload.UserId);
        Assert.Equal("trusted-user", payload.DisplayName);
        Assert.True(payload.IsTyping);
    }

    [Fact]
    public async Task SetTyping_PrivateClient_RoutesOnlyToAssignedAdminUser()
    {
        const long assignedAdminId = 91;
        var fixture = CreateFixture(CreateClientPrincipal("trusted-user"));
        fixture.Service.AccessResolver = (conversationId, _) =>
            new ConversationAccess(
                conversationId,
                ClientId,
                ConversationTypes.SupportPrivate,
                100,
                ClientUserId: UserId,
                AdminUserId: assignedAdminId);

        await fixture.Hub.SetTyping(ConversationId, true);

        Assert.Equal(
            [RealtimeUserIds.Admin(assignedAdminId)],
            fixture.Clients.UserSelections);
        Assert.Empty(fixture.Clients.GroupExceptSelections);
        Assert.Single(fixture.Clients.Proxy.TypingCalls);
    }

    [Fact]
    public async Task SetTyping_PrivateAdmin_RoutesOnlyToExactTenantClientUser()
    {
        const int clientUserId = 82;
        var fixture = CreateFixture(CreateAdminPrincipal());
        fixture.Service.AccessResolver = (conversationId, _) =>
            new ConversationAccess(
                conversationId,
                ClientId,
                ConversationTypes.SupportPrivate,
                100,
                ClientUserId: clientUserId,
                AdminUserId: UserId);

        await fixture.Hub.SetTyping(ConversationId, false);

        Assert.Equal(
            [RealtimeUserIds.Client(ClientId, clientUserId)],
            fixture.Clients.UserSelections);
        Assert.Empty(fixture.Clients.GroupExceptSelections);
        Assert.False(Assert.Single(fixture.Clients.Proxy.TypingCalls).IsTyping);
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
        Assert.Empty(fixture.Clients.Proxy.TypingCalls);
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
    public async Task JoinConversation_PrivateFeatureDisabled_DeniesMappedPrivateConversation()
    {
        var fixture = CreateFixture(CreateClientPrincipal(), privateEnabled: false);
        fixture.Service.AccessResolver = (_, _) => CreateAccess(ConversationId, ClientId) with
        {
            InstructionTypeId = ConversationTypes.SupportPrivate,
            ClientUserId = UserId,
            AdminUserId = 8
        };

        var exception = await Assert.ThrowsAsync<HubException>(
            () => fixture.Hub.JoinConversation(ConversationId));

        Assert.Equal("Conversation unavailable.", exception.Message);
        Assert.Empty(fixture.Groups.AddCalls);
    }

    [Fact]
    public async Task LeaveConversation_PrivateFeatureDisabled_DeniesWithoutRemovingGroupMembership()
    {
        var fixture = CreateFixture(CreateClientPrincipal(), privateEnabled: false);
        fixture.Service.AccessResolver = (_, _) => CreateAccess(ConversationId, ClientId) with
        {
            InstructionTypeId = ConversationTypes.SupportPrivate,
            ClientUserId = UserId,
            AdminUserId = 8
        };

        var exception = await Assert.ThrowsAsync<HubException>(
            () => fixture.Hub.LeaveConversation(ConversationId));

        Assert.Equal("Conversation unavailable.", exception.Message);
        Assert.Single(fixture.Service.AccessCalls);
        Assert.Empty(fixture.Groups.RemoveCalls);
    }

    [Fact]
    public async Task SetTyping_PrivateFeatureDisabled_DeniesWithoutBroadcasting()
    {
        var fixture = CreateFixture(CreateClientPrincipal(), privateEnabled: false);
        fixture.Service.AccessResolver = (_, _) => CreateAccess(ConversationId, ClientId) with
        {
            InstructionTypeId = ConversationTypes.SupportPrivate,
            ClientUserId = UserId,
            AdminUserId = 8
        };

        var exception = await Assert.ThrowsAsync<HubException>(
            () => fixture.Hub.SetTyping(ConversationId, true));

        Assert.Equal("Conversation unavailable.", exception.Message);
        Assert.Single(fixture.Service.AccessCalls);
        Assert.Empty(fixture.Clients.UserSelections);
        Assert.Empty(fixture.Clients.GroupExceptSelections);
        Assert.Empty(fixture.Clients.Proxy.TypingCalls);
    }

    private static HubFixture CreateFixture(
        ClaimsPrincipal principal,
        bool privateEnabled = true)
    {
        var service = new RecordingConversationService();
        var groups = new RecordingGroupManager();
        var clients = new RecordingHubCallerClients();
        var hub = new ChatHub(
            service,
            Options.Create(new MessagingFeatureOptions
            {
                GroupEnabled = true,
                PrivateEnabled = privateEnabled
            }),
            NullLogger<ChatHub>.Instance)
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

    private sealed record HubFixture(
        ChatHub Hub,
        RecordingConversationService Service,
        RecordingGroupManager Groups,
        RecordingHubCallerClients Clients);

    private sealed record AccessCall(long ConversationId, ConversationActor Actor);

    private sealed class RecordingConversationService : IConversationService
    {
        public Func<long, ConversationActor, ConversationAccess?> AccessResolver { get; set; } =
            (_, _) => null;

        public List<AccessCall> AccessCalls { get; } = [];

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
            return Task.FromResult<ConversationMessage?>(null);
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

    private sealed class RecordingChatClient : IChatClient
    {
        public List<RealtimeEnvelope<ConversationMessage>> MessageCalls { get; } = [];

        public List<TypingChangedEvent> TypingCalls { get; } = [];

        public Task MessageCreated(RealtimeEnvelope<ConversationMessage> message)
        {
            MessageCalls.Add(message);
            return Task.CompletedTask;
        }

        public Task ConversationChanged(
            RealtimeEnvelope<ConversationChangedEvent> conversation) => Task.CompletedTask;

        public Task NotificationChanged(RealtimeEnvelope<NotificationChangedEvent> notification) => Task.CompletedTask;

        public Task TypingChanged(TypingChangedEvent typing)
        {
            TypingCalls.Add(typing);
            return Task.CompletedTask;
        }
    }

    private sealed record GroupExceptSelection(
        string GroupName,
        IReadOnlyList<string> ExcludedConnectionIds);

    private sealed class RecordingHubCallerClients : IHubCallerClients<IChatClient>
    {
        public RecordingChatClient Proxy { get; } = new();

        public List<GroupExceptSelection> GroupExceptSelections { get; } = [];
        public List<string> UserSelections { get; } = [];

        public IChatClient All => Proxy;

        public IChatClient Others => Proxy;

        public IChatClient Caller => Proxy;

        public IChatClient OthersInGroup(string groupName) => Proxy;

        public IChatClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IChatClient Client(string connectionId) => Proxy;

        public IChatClient Clients(IReadOnlyList<string> connectionIds) => Proxy;

        public IChatClient Group(string groupName) => Proxy;

        public IChatClient GroupExcept(
            string groupName,
            IReadOnlyList<string> excludedConnectionIds)
        {
            GroupExceptSelections.Add(
                new GroupExceptSelection(groupName, excludedConnectionIds.ToArray()));
            return Proxy;
        }

        public IChatClient Groups(IReadOnlyList<string> groupNames) => Proxy;

        public IChatClient User(string userId)
        {
            UserSelections.Add(userId);
            return Proxy;
        }

        public IChatClient Users(IReadOnlyList<string> userIds) => Proxy;
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
