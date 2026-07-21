namespace CBSSupport.API.Tests.Integration;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgreSqlIntegrationFactAttribute : FactAttribute
{
    public const string ConnectionStringEnvironmentVariable = "CBSSUPPORT_TEST_POSTGRES";

    public PostgreSqlIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)))
        {
            Skip = $"Set {ConnectionStringEnvironmentVariable} to a PostgreSQL admin connection string.";
        }
    }
}
