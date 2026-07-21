namespace CBSSupport.API.Security;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Client = "Client";
}

public static class Policies
{
    public const string AdminOnly = "AdminOnly";
    public const string ClientOnly = "ClientOnly";
    public const string AdminOrClient = "AdminOrClient";
}

public static class CustomClaimTypes
{
    public const string ClientId = "client_id";
    public const string SecurityStamp = "security_stamp";

    // Remove after existing cookies have expired and every issuer emits client_id.
    public const string LegacyClientId = "ClientId";
}

public static class JwtClaimTypes
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Role = "role";
}
