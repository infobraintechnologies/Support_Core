using System.Diagnostics.Metrics;

namespace CBSSupport.API.Security;

public static class LoginThrottleMetrics
{
    private static readonly Meter Meter = new("CBSSupport.Security.LoginThrottle", "1.0.0");

    public static readonly Counter<long> Checks = Meter.CreateCounter<long>(
        "cbs_login_throttle_checks",
        description: "Login throttle checks completed against the distributed store.");

    public static readonly Counter<long> Blocked = Meter.CreateCounter<long>(
        "cbs_login_throttle_blocked",
        description: "Login attempts rejected by the distributed throttle.");

    public static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        "cbs_login_throttle_failures",
        description: "Failed login attempts recorded by the distributed throttle.");

    public static readonly Counter<long> Resets = Meter.CreateCounter<long>(
        "cbs_login_throttle_resets",
        description: "Successful-login throttle resets.");

    public static readonly Counter<long> StoreFailures = Meter.CreateCounter<long>(
        "cbs_login_throttle_store_failures",
        description: "Distributed throttle store failures.");
}
