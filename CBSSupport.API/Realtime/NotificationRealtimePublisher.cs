using CBSSupport.API.Hubs;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.SignalR;

namespace CBSSupport.API.Realtime;

public interface INotificationRealtimePublisher
{
    Task PublishAsync(NotificationRecipient recipient, NotificationChangedEvent change, CancellationToken cancellationToken = default);
}

/// <summary>Publishes only server-committed recipient state to every device of that recipient.</summary>
public sealed class NotificationRealtimePublisher(IHubContext<ChatHub, IChatClient> hubContext) : INotificationRealtimePublisher
{
    public Task PublishAsync(NotificationRecipient recipient, NotificationChangedEvent change, CancellationToken cancellationToken = default)
    {
        var userId = recipient.IsAdmin
            ? RealtimeUserIds.Admin(recipient.UserId)
            : RealtimeUserIds.Client(recipient.ClientId!.Value, recipient.UserId);
        var envelope = new RealtimeEnvelope<NotificationChangedEvent>(
            Guid.NewGuid(),
            1,
            "NotificationChanged",
            DateTime.UtcNow,
            change.Notification?.CaseId ?? 0,
            0,
            change);
        return hubContext.Clients.User(userId).NotificationChanged(envelope);
    }
}
