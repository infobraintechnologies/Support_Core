using CBSSupport.API.Hubs;
using CBSSupport.Shared.Contracts;
using CBSSupport.API.Configuration;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace CBSSupport.API.Realtime;

public sealed class SignalRConversationRealtimePublisher(
    IHubContext<ChatHub, IChatClient> hubContext,
    INotificationService notifications,
    IOptions<MessagingFeatureOptions> featureOptions) : IConversationRealtimePublisher
{
    private readonly MessagingFeatureOptions _features = featureOptions.Value;

    public SignalRConversationRealtimePublisher(IHubContext<ChatHub, IChatClient> hubContext)
        : this(hubContext, new NoopNotificationService(), Options.Create(new MessagingFeatureOptions
        {
            GroupEnabled = true,
            PrivateEnabled = true
        }))
    {
    }

    public SignalRConversationRealtimePublisher(
        IHubContext<ChatHub, IChatClient> hubContext,
        IOptions<MessagingFeatureOptions> featureOptions)
        : this(hubContext, new NoopNotificationService(), featureOptions)
    {
    }

    public async Task PublishAsync(
        ConversationOutboxItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if ((string.Equals(item.ConversationKind, "Group", StringComparison.OrdinalIgnoreCase)
                && !_features.GroupEnabled)
            || (string.Equals(item.ConversationKind, "Private", StringComparison.OrdinalIgnoreCase)
                && !_features.PrivateEnabled))
        {
            return;
        }

        await (item.EventType switch
        {
            "MessageCreated" => PublishMessageCreatedAsync(item),
            "ConversationTransferred" or "ConversationArchived" or "ConversationApproved"
                or "TicketResolved" or "TicketReopened" or "InquiryCompleted" or "InquiryReopened"
                or "TicketUpdated" =>
                PublishConversationChangedAsync(item),
            _ => throw new InvalidOperationException(
                $"Unsupported conversation outbox event type '{item.EventType}'.")
        });
        await PublishNotificationChangesAsync(item, cancellationToken);
    }

    private Task PublishMessageCreatedAsync(ConversationOutboxItem item)
    {
        var message = item.Message
            ?? throw new InvalidOperationException("MessageCreated outbox item has no canonical message.");
        var envelope = new RealtimeEnvelope<ConversationMessage>(
            item.EventId,
            item.SchemaVersion,
            item.EventType,
            item.OccurredAt,
            item.ConversationId,
            message.Sequence,
            message);

        return GetAudience(item).MessageCreated(envelope);
    }

    private Task PublishConversationChangedAsync(ConversationOutboxItem item)
    {
        var change = new ConversationChangedEvent(
            item.EventType,
            item.ConversationKind,
            item.ClientId,
            item.ClientUserId,
            item.AdminUserId);
        var envelope = new RealtimeEnvelope<ConversationChangedEvent>(
            item.EventId,
            item.SchemaVersion,
            item.EventType,
            item.OccurredAt,
            item.ConversationId,
            item.Message?.Sequence ?? 0,
            change);

        return GetAudience(item).ConversationChanged(envelope);
    }

    private IChatClient GetAudience(ConversationOutboxItem item)
    {
        if (string.Equals(item.ConversationKind, "Private", StringComparison.OrdinalIgnoreCase))
        {
            var users = new List<string>(2);
            if (item.AdminUserId is > 0)
            {
                users.Add(RealtimeUserIds.Admin(item.AdminUserId.Value));
            }
            if (item.ClientUserId is > 0)
            {
                users.Add(RealtimeUserIds.Client(item.ClientId, item.ClientUserId.Value));
            }
            if (users.Count == 0)
            {
                throw new InvalidOperationException(
                    "Private conversation outbox item has no exact recipients.");
            }

            return hubContext.Clients.Users(users);
        }

        if (string.Equals(item.ConversationKind, ConversationKinds.Group, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.ConversationKind, ConversationKinds.Ticket, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.ConversationKind, ConversationKinds.Inquiry, StringComparison.OrdinalIgnoreCase))
        {
            return hubContext.Clients.Groups(
                [RealtimeGroupNames.Admins, RealtimeGroupNames.Tenant(item.ClientId)]);
        }

        throw new InvalidOperationException(
            $"Unsupported conversation audience kind '{item.ConversationKind}'.");
    }

    private async Task PublishNotificationChangesAsync(ConversationOutboxItem item, CancellationToken cancellationToken)
    {
        var deliveries = await notifications.GetChangesForEventAsync(item.EventId, cancellationToken);
        foreach (var delivery in deliveries)
        {
            var userId = delivery.IsAdmin
                ? RealtimeUserIds.Admin(delivery.RecipientUserId)
                : RealtimeUserIds.Client(delivery.ClientId, delivery.RecipientUserId);
            var envelope = new RealtimeEnvelope<NotificationChangedEvent>(
                item.EventId,
                1,
                "NotificationChanged",
                item.OccurredAt,
                delivery.Change.Notification!.CaseId,
                0,
                delivery.Change);
            await hubContext.Clients.User(userId).NotificationChanged(envelope);
        }
    }

    private sealed class NoopNotificationService : INotificationService
    {
        public Task<NotificationPage> ListAsync(NotificationRecipient recipient, int pageSize, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NotificationChangedEvent?> MarkReadAsync(NotificationRecipient recipient, long notificationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NotificationBulkReadResult> MarkAllReadAsync(NotificationRecipient recipient, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NotificationDelivery>> GetChangesForEventAsync(Guid eventId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<NotificationDelivery>>([]);
    }
}
