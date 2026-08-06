using System.Globalization;
using System.Security.Claims;
using CBSSupport.API.Security;
using CBSSupport.Shared.Data;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CBSSupport.API.Tests.Integration;

internal sealed class SignalRPostgreSqlFixture : IAsyncDisposable
{
    private const long ClientId = 501;
    private const string PasswordHash = "integration-password-hash";
    private const string PasswordSalt = "integration-password-salt";
    private readonly string _adminConnectionString;
    private readonly string _databaseName;

    private SignalRPostgreSqlFixture(
        string adminConnectionString,
        string databaseName,
        string applicationConnectionString,
        TestApplicationFactory factory)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        ApplicationConnectionString = applicationConnectionString;
        Factory = factory;
    }

    public const long ConversationId = 9001;
    public const long TicketConversationId = 9002;
    public const long InquiryConversationId = 9003;
    public const long TicketReplyId = 9004;

    public static SeededClient RevokedClient { get; } =
        new(101, ClientId, "integration-client-a", "Integration Client A");

    public static SeededClient ObserverClient { get; } =
        new(102, ClientId, "integration-client-b", "Integration Client B");

    public string ApplicationConnectionString { get; }

    public TestApplicationFactory Factory { get; }

    public static async Task<SignalRPostgreSqlFixture> CreateAsync()
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlIntegrationFactAttribute.ConnectionStringEnvironmentVariable)!;
        var adminBuilder = new NpgsqlConnectionStringBuilder(configuredConnectionString)
        {
            Pooling = false
        };
        if (string.IsNullOrWhiteSpace(adminBuilder.Database))
        {
            adminBuilder.Database = "postgres";
        }

        var databaseName = $"cbssupport_it_{Guid.NewGuid():N}";
        await using (var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var createDatabase = adminConnection.CreateCommand();
            createDatabase.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await createDatabase.ExecuteNonQueryAsync();
        }

        var applicationBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Database = databaseName,
            Pooling = false
        };
        TestApplicationFactory? factory = null;
        try
        {
            await InitializeDatabaseAsync(applicationBuilder.ConnectionString);
            factory = new TestApplicationFactory(applicationBuilder.ConnectionString);
            _ = factory.Server;
            return new SignalRPostgreSqlFixture(
                adminBuilder.ConnectionString,
                databaseName,
                applicationBuilder.ConnectionString,
                factory);
        }
        catch
        {
            factory?.Dispose();
            await DropDatabaseAsync(adminBuilder.ConnectionString, databaseName);
            throw;
        }
    }

    public HubConnection CreateHubConnection(SeededClient client)
    {
        var cookieHeader = CreateAuthenticationCookie(client);
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(Factory.Server.BaseAddress, "/chathub"),
                options =>
                {
                    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                    options.Headers["Cookie"] = cookieHeader;
                })
            .Build();
    }

    public async Task RevokeClientAsync(long userId)
    {
        await using var connection = new NpgsqlConnection(ApplicationConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE internal.support_users
            SET status = FALSE,
                deactive_date = now()
            WHERE id = @userId;
            """;
        command.Parameters.AddWithValue("userId", userId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    public async ValueTask DisposeAsync()
    {
        Factory.Dispose();
        NpgsqlConnection.ClearAllPools();
        await DropDatabaseAsync(_adminConnectionString, _databaseName);
    }

    private string CreateAuthenticationCookie(SeededClient client)
    {
        using var scope = Factory.Services.CreateScope();
        var stamps = scope.ServiceProvider.GetRequiredService<IAccountSecurityStampService>();
        var cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, client.UserId.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, client.DisplayName),
            new Claim(ClaimTypes.Role, Roles.Client),
            new Claim(CustomClaimTypes.ClientId, client.ClientId.ToString(CultureInfo.InvariantCulture)),
            new Claim(
                CustomClaimTypes.SecurityStamp,
                stamps.Create(GetSecurityStamp(client.UserId)))
        };
        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
        };
        properties.Items[CookiePrincipalValidationEvents.LastValidatedUtcProperty] =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            properties,
            CookieAuthenticationDefaults.AuthenticationScheme);
        var protectedTicket = cookieOptions.TicketDataFormat.Protect(ticket);
        return $"{cookieOptions.Cookie.Name}={protectedTicket}";
    }

    private static byte[] GetSecurityStamp(int userId) =>
        userId switch
        {
            101 => Enumerable.Repeat((byte)0x01, 32).ToArray(),
            102 => Enumerable.Repeat((byte)0x02, 32).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(userId))
        };

    private static async Task InitializeDatabaseAsync(string connectionString)
    {
        const string sql = """
            CREATE SCHEMA internal;
            CREATE SCHEMA digital;

            CREATE TABLE internal.support_users (
                id integer PRIMARY KEY,
                client_id integer NOT NULL,
                user_name text NOT NULL,
                full_name text NOT NULL,
                password_hash text NOT NULL,
                password_salt text NOT NULL,
                security_stamp bytea NOT NULL,
                status boolean NOT NULL,
                deactive_date timestamp with time zone NULL
            );

            CREATE TABLE digital.instructions (
                id bigint PRIMARY KEY,
                client_id bigint NULL,
                inst_type_id smallint NOT NULL,
                inst_category_id smallint NOT NULL,
                instruction_id bigint NULL
            );

            CREATE TABLE digital.conversation_access (
                conversation_id bigint PRIMARY KEY,
                client_id bigint NOT NULL,
                conversation_kind varchar(16) NOT NULL,
                state varchar(16) NOT NULL,
                client_user_id integer NULL,
                admin_user_id integer NULL,
                version bigint NOT NULL
            );

            INSERT INTO internal.support_users (
                id, client_id, user_name, full_name,
                password_hash, password_salt, security_stamp, status, deactive_date)
            VALUES
                (101, 501, 'integration-client-a', 'Integration Client A',
                 'integration-password-hash', 'integration-password-salt', decode(repeat('01', 32), 'hex'), TRUE, NULL),
                (102, 501, 'integration-client-b', 'Integration Client B',
                 'integration-password-hash', 'integration-password-salt', decode(repeat('02', 32), 'hex'), TRUE, NULL);

            INSERT INTO digital.instructions (
                id, client_id, inst_type_id, inst_category_id, instruction_id)
            VALUES
                (9001, 501, 100, 100, 9001),
                (9002, 501, 110, 101, 9002),
                (9003, 501, 121, 102, 9003),
                (9004, 501, 110, 101, 9002);

            INSERT INTO digital.conversation_access (
                conversation_id, client_id, conversation_kind, state,
                client_user_id, admin_user_id, version)
            VALUES (9001, 501, 'Group', 'Active', NULL, NULL, 1);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(
        string adminConnectionString,
        string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    internal sealed class TestApplicationFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUserRepository>();
                services.RemoveAll<IConversationRepository>();
                services.RemoveAll<IChatService>();
                services.AddSingleton<IUserRepository>(new UserRepository(connectionString));
                services.AddSingleton<IConversationRepository>(
                    new ConversationRepository(
                        connectionString,
                        attachmentsEnabled: false));
                services.AddSingleton<IChatService>(
                    new ChatService(connectionString, NullLogger<ChatService>.Instance));
            });
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = connectionString,
                        ["Jwt:Enabled"] = "false",
                        ["Security:PasswordHashing:Pepper"] = "test-company-pepper"
                    }));
        }
    }

    internal sealed record SeededClient(
        int UserId,
        long ClientId,
        string Username,
        string DisplayName);
}
