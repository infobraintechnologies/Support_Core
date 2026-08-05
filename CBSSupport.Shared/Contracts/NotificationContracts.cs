namespace CBSSupport.Shared.Contracts;

/// <summary>Server-authoritative state for one notification recipient.</summary>
public sealed record NotificationResponse(
    long Id,
    long CaseId,
    string EventType,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    string Title,
    string Message);

public sealed record NotificationPage(
    IReadOnlyList<NotificationResponse> Items,
    string? NextCursor,
    long UnreadCount);

/// <summary>Published only to the notification's authenticated recipient.</summary>
public sealed record NotificationChangedEvent(
    NotificationResponse? Notification,
    long UnreadCount);

public sealed record NotificationBulkReadResult(long Count, long UnreadCount);

/// <summary>Internal delivery address resolved from a committed notification row.</summary>
public sealed record NotificationDelivery(
    bool IsAdmin,
    long ClientId,
    int RecipientUserId,
    NotificationChangedEvent Change);
