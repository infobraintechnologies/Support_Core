using CBSSupport.API.Security;
using CBSSupport.API.Realtime;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Authorize(Policy = Policies.AdminOrClient)]
public sealed class NotificationsController(
    INotificationService notifications,
    INotificationRealtimePublisher realtime) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NotificationPage>> List(
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Ok(await notifications.ListAsync(CurrentRecipient(), limit, cursor, cancellationToken));

    [HttpPut("{notificationId:long}/read")]
    public async Task<ActionResult<NotificationChangedEvent>> MarkRead(
        long notificationId,
        CancellationToken cancellationToken)
    {
        var recipient = CurrentRecipient();
        var changed = await notifications.MarkReadAsync(recipient, notificationId, cancellationToken);
        if (changed is null) return NotFound();
        await realtime.PublishAsync(recipient, changed, cancellationToken);
        return Ok(changed);
    }

    [HttpPut("read-all")]
    public async Task<ActionResult<object>> MarkAllRead(CancellationToken cancellationToken)
    {
        var recipient = CurrentRecipient();
        var result = await notifications.MarkAllReadAsync(recipient, cancellationToken);
        await realtime.PublishAsync(recipient, new NotificationChangedEvent(null, result.UnreadCount), cancellationToken);
        return Ok(result);
    }

    private NotificationRecipient CurrentRecipient()
    {
        var actor = User.GetConversationActor();
        return NotificationRecipient.FromActor(actor);
    }
}
