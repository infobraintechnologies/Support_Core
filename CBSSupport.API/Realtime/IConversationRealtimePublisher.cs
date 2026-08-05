using CBSSupport.Shared.Contracts;

namespace CBSSupport.API.Realtime;

public interface IConversationRealtimePublisher
{
    Task PublishAsync(
        ConversationOutboxItem item,
        CancellationToken cancellationToken = default);
}
