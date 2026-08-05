using CBSSupport.API.Hubs;
using CBSSupport.API.Realtime;
using CBSSupport.API.Configuration;
using CBSSupport.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace CBSSupport.API.Tests.Integration;

public sealed class SignalRAudienceBroadcastIntegrationTests
{
    private const long ConversationId = 9001;
    private const long ClientId = 42;
    private const int ClientUserId = 9;
    private const long AdminUserId = 7;

    [Fact]
    public async Task PublishMessage_GroupConversation_TargetsTrustedTenantAndAdminGroups()
    {
        var context = new RecordingHubContext();
        var publisher = new SignalRConversationRealtimePublisher(context);
        var item = CreateItem("Group");

        await publisher.PublishAsync(item);

        Assert.Equal(
            [[RealtimeGroupNames.Admins, RealtimeGroupNames.Tenant(ClientId)]],
            context.ClientsRecorder.GroupSets);
        Assert.Empty(context.ClientsRecorder.UserSets);
        var envelope = Assert.Single(context.ClientsRecorder.Proxy.MessageCalls);
        Assert.Equal(item.EventId, envelope.EventId);
        Assert.Equal(item.Message, envelope.Data);
        Assert.Equal(item.Message!.Sequence, envelope.Sequence);
    }

    [Fact]
    public async Task PublishMessage_PrivateConversation_TargetsOnlyExactNamespacedParticipants()
    {
        var context = new RecordingHubContext();
        var publisher = new SignalRConversationRealtimePublisher(context);

        await publisher.PublishAsync(CreateItem("Private"));

        Assert.Equal(
            [[
                RealtimeUserIds.Admin(AdminUserId),
                RealtimeUserIds.Client(ClientId, ClientUserId)
            ]],
            context.ClientsRecorder.UserSets);
        Assert.Empty(context.ClientsRecorder.GroupSets);
        Assert.Single(context.ClientsRecorder.Proxy.MessageCalls);
    }

    [Fact]
    public async Task PublishMessage_PrivateConversationWithoutParticipants_IsRejected()
    {
        var context = new RecordingHubContext();
        var publisher = new SignalRConversationRealtimePublisher(context);
        var item = CreateItem("Private") with
        {
            ClientUserId = null,
            AdminUserId = null
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.PublishAsync(item));

        Assert.Empty(context.ClientsRecorder.Proxy.MessageCalls);
    }

    [Fact]
    public async Task PublishMessage_PrivateFeatureDisabled_DoesNotSelectAudience()
    {
        var context = new RecordingHubContext();
        var publisher = new SignalRConversationRealtimePublisher(
            context,
            Options.Create(new MessagingFeatureOptions
            {
                GroupEnabled = true,
                PrivateEnabled = false
            }));

        await publisher.PublishAsync(CreateItem("Private"));

        Assert.Empty(context.ClientsRecorder.UserSets);
        Assert.Empty(context.ClientsRecorder.Proxy.MessageCalls);
    }

    [Fact]
    public async Task PublishApprovedPrivateConversation_TargetsOnlyApprovedParticipants()
    {
        var context = new RecordingHubContext();
        var publisher = new SignalRConversationRealtimePublisher(context);
        var item = CreateItem("Private") with
        {
            EventType = "ConversationApproved",
            MessageId = null,
            Message = null
        };

        await publisher.PublishAsync(item);

        Assert.Equal(
            [[
                RealtimeUserIds.Admin(AdminUserId),
                RealtimeUserIds.Client(ClientId, ClientUserId)
            ]],
            context.ClientsRecorder.UserSets);
        Assert.Empty(context.ClientsRecorder.GroupSets);
        var envelope = Assert.Single(context.ClientsRecorder.Proxy.ConversationCalls);
        Assert.Equal(item.EventId, envelope.EventId);
        Assert.Equal("ConversationApproved", envelope.EventType);
        Assert.Equal(AdminUserId, envelope.Data.AdminUserId);
        Assert.Equal(ClientUserId, envelope.Data.ClientUserId);
    }

    private static ConversationOutboxItem CreateItem(string kind)
    {
        var message = new ConversationMessage(
            7001,
            ConversationId,
            "Audience delivery",
            new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc),
            new ConversationSender(AdminUserId, "Test Admin", "Admin"),
            ClientMessageId: Guid.NewGuid(),
            Sequence: 12,
            Attachments: []);
        return new ConversationOutboxItem(
            Guid.NewGuid(),
            ConversationId,
            message.Id,
            "MessageCreated",
            1,
            message.SentAt,
            1,
            ClientId,
            kind,
            ConversationStates.Active,
            ClientUserId,
            AdminUserId,
            1,
            ConversationStates.Active,
            1,
            message);
    }

    private sealed class RecordingHubContext : IHubContext<ChatHub, IChatClient>
    {
        public RecordingHubClients ClientsRecorder { get; } = new();

        public IHubClients<IChatClient> Clients => ClientsRecorder;

        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class RecordingHubClients : IHubClients<IChatClient>
    {
        public RecordingChatClient Proxy { get; } = new();

        public List<IReadOnlyList<string>> GroupSets { get; } = [];

        public List<IReadOnlyList<string>> UserSets { get; } = [];

        public IChatClient All => Proxy;

        public IChatClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IChatClient Client(string connectionId) => Proxy;

        public IChatClient Clients(IReadOnlyList<string> connectionIds) => Proxy;

        public IChatClient Group(string groupName)
        {
            GroupSets.Add([groupName]);
            return Proxy;
        }

        public IChatClient GroupExcept(
            string groupName,
            IReadOnlyList<string> excludedConnectionIds) => Group(groupName);

        public IChatClient Groups(IReadOnlyList<string> groupNames)
        {
            GroupSets.Add(groupNames.ToArray());
            return Proxy;
        }

        public IChatClient User(string userId)
        {
            UserSets.Add([userId]);
            return Proxy;
        }

        public IChatClient Users(IReadOnlyList<string> userIds)
        {
            UserSets.Add(userIds.ToArray());
            return Proxy;
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<RealtimeEnvelope<ConversationMessage>> MessageCalls { get; } = [];

        public List<RealtimeEnvelope<ConversationChangedEvent>> ConversationCalls { get; } = [];

        public Task MessageCreated(RealtimeEnvelope<ConversationMessage> message)
        {
            MessageCalls.Add(message);
            return Task.CompletedTask;
        }

        public Task ConversationChanged(
            RealtimeEnvelope<ConversationChangedEvent> conversation)
        {
            ConversationCalls.Add(conversation);
            return Task.CompletedTask;
        }

        public Task NotificationChanged(RealtimeEnvelope<NotificationChangedEvent> notification) => Task.CompletedTask;

        public Task TypingChanged(TypingChangedEvent typing) => Task.CompletedTask;
    }

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
