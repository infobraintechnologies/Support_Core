using System.Globalization;

namespace CBSSupport.API.Realtime;

public static class RealtimeUserIds
{
    public static string Admin(long userId) =>
        $"admin:{userId.ToString(CultureInfo.InvariantCulture)}";

    public static string Client(long clientId, long userId) =>
        $"client:{clientId.ToString(CultureInfo.InvariantCulture)}:{userId.ToString(CultureInfo.InvariantCulture)}";
}
