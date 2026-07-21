using System.Globalization;

namespace CBSSupport.API.Hubs;

public static class RealtimeGroupNames
{
    public const string Admins = "role:admin";

    public static string Conversation(long conversationId) =>
        $"conversation:{conversationId.ToString(CultureInfo.InvariantCulture)}";

    public static string Tenant(long clientId) =>
        $"tenant:{clientId.ToString(CultureInfo.InvariantCulture)}";
}
