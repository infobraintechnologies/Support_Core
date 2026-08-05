using CBSSupport.Shared.Contracts;

namespace CBSSupport.API.Realtime;

public interface IChatClient
{
    Task MessageCreated(RealtimeEnvelope<ConversationMessage> message);

    Task ConversationChanged(RealtimeEnvelope<ConversationChangedEvent> conversation);

    Task TypingChanged(TypingChangedEvent typing);
}
