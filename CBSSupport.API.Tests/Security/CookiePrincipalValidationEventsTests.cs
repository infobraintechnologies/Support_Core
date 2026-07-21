using System.Security.Claims;
using CBSSupport.API.Security;
using CBSSupport.API.Tests.TestDoubles;
using CBSSupport.Shared.Data;
using CBSSupport.Shared.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Security;

public sealed class CookiePrincipalValidationEventsTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly IAccountSecurityStampService SecurityStamps =
        new FakeAccountSecurityStampService();

    [Fact]
    public async Task ValidatePrincipal_ActiveAdministratorWithCurrentStamp_RenewsCookie()
    {
        var user = CreateAdminUser();
        var repository = new StubUserRepository { AdminById = user };
        var context = CreateContext(CreateAdminPrincipal(user));

        await CreateEvents(repository).ValidatePrincipal(context);

        Assert.NotNull(context.Principal);
        Assert.True(context.ShouldRenew);
        Assert.Equal(1, repository.AdminByIdCalls);
        Assert.True(context.Properties.Items.ContainsKey(
            CookiePrincipalValidationEvents.LastValidatedUtcProperty));
    }

    [Fact]
    public async Task ValidatePrincipal_RecentlyValidatedCookie_SkipsRepositoryLookup()
    {
        var user = CreateAdminUser();
        var repository = new StubUserRepository();
        var properties = new AuthenticationProperties();
        properties.Items[CookiePrincipalValidationEvents.LastValidatedUtcProperty] =
            Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var context = CreateContext(CreateAdminPrincipal(user), properties);

        await CreateEvents(repository).ValidatePrincipal(context);

        Assert.NotNull(context.Principal);
        Assert.False(context.ShouldRenew);
        Assert.Equal(0, repository.AdminByIdCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ValidatePrincipal_InactiveAdministrator_RejectsCookie(
        bool status,
        bool hasDeactiveDate)
    {
        var issuedUser = CreateAdminUser();
        var currentUser = CreateAdminUser(
            status: status,
            deactiveDate: hasDeactiveDate ? Now : null);
        var repository = new StubUserRepository { AdminById = currentUser };
        var authentication = new RecordingAuthenticationService();
        var context = CreateContext(CreateAdminPrincipal(issuedUser), authentication: authentication);

        await CreateEvents(repository).ValidatePrincipal(context);

        Assert.Null(context.Principal);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, authentication.SignOutScheme);
    }

    [Fact]
    public async Task ValidatePrincipal_AdministratorPasswordChanged_RejectsCookie()
    {
        var issuedUser = CreateAdminUser(passwordHash: "old-hash", passwordSalt: "old-salt");
        var currentUser = CreateAdminUser(passwordHash: "new-hash", passwordSalt: "new-salt");
        var authentication = new RecordingAuthenticationService();
        var context = CreateContext(
            CreateAdminPrincipal(issuedUser),
            authentication: authentication);

        await CreateEvents(new StubUserRepository { AdminById = currentUser })
            .ValidatePrincipal(context);

        Assert.Null(context.Principal);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, authentication.SignOutScheme);
    }

    [Fact]
    public async Task ValidatePrincipal_MissingAccount_RejectsCookie()
    {
        var user = CreateAdminUser();
        var authentication = new RecordingAuthenticationService();
        var context = CreateContext(CreateAdminPrincipal(user), authentication: authentication);

        await CreateEvents(new StubUserRepository()).ValidatePrincipal(context);

        Assert.Null(context.Principal);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, authentication.SignOutScheme);
    }

    [Theory]
    [MemberData(nameof(MalformedPrincipals))]
    public async Task ValidatePrincipal_MalformedIdentityClaims_RejectsCookie(ClaimsPrincipal principal)
    {
        var authentication = new RecordingAuthenticationService();
        var context = CreateContext(principal, authentication: authentication);

        await CreateEvents(new StubUserRepository()).ValidatePrincipal(context);

        Assert.Null(context.Principal);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, authentication.SignOutScheme);
    }

    [Fact]
    public async Task ValidatePrincipal_ClientCookie_UsesClaimDerivedTenantScope()
    {
        var user = CreateClientUser();
        var repository = new StubUserRepository { ClientById = user };
        var context = CreateContext(CreateClientPrincipal(user));

        await CreateEvents(repository).ValidatePrincipal(context);

        Assert.NotNull(context.Principal);
        Assert.Equal((user.ClientId, user.Id), repository.LastClientLookup);
    }

    [Fact]
    public async Task ValidatePrincipal_ClientPasswordChanged_RejectsCookie()
    {
        var issuedUser = CreateClientUser();
        var currentUser = CreateClientUser();
        currentUser.PasswordHash = "replacement-hash";
        currentUser.PasswordSalt = "replacement-salt";
        var authentication = new RecordingAuthenticationService();
        var context = CreateContext(
            CreateClientPrincipal(issuedUser),
            authentication: authentication);

        await CreateEvents(new StubUserRepository { ClientById = currentUser })
            .ValidatePrincipal(context);

        Assert.Null(context.Principal);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, authentication.SignOutScheme);
    }

    [Fact]
    public async Task AccountPrincipalValidator_RawJwtAdminClaims_UsesDatabaseCredentialState()
    {
        var user = CreateAdminUser();
        var repository = new StubUserRepository { AdminById = user };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(JwtClaimTypes.Subject, user.Id.ToString()),
                new Claim(JwtClaimTypes.Role, Roles.Admin),
                new Claim(
                    CustomClaimTypes.SecurityStamp,
                    SecurityStamps.Create(user.PasswordHash, user.PasswordSalt))
            ],
            "Bearer",
            JwtClaimTypes.Name,
            JwtClaimTypes.Role));

        var isValid = await new AccountPrincipalValidator(repository, SecurityStamps)
            .ValidateAsync(principal);

        Assert.True(isValid);
        Assert.Equal(1, repository.AdminByIdCalls);
    }

    public static TheoryData<ClaimsPrincipal> MalformedPrincipals()
    {
        var user = CreateAdminUser();
        var validStamp = SecurityStamps.Create(user.PasswordHash, user.PasswordSalt);
        return new TheoryData<ClaimsPrincipal>
        {
            CreatePrincipal(
                new Claim(ClaimTypes.Role, Roles.Admin),
                new Claim(CustomClaimTypes.SecurityStamp, validStamp)),
            CreatePrincipal(
                new Claim(ClaimTypes.NameIdentifier, "not-a-number"),
                new Claim(ClaimTypes.Role, Roles.Admin),
                new Claim(CustomClaimTypes.SecurityStamp, validStamp)),
            CreatePrincipal(
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Role, Roles.Admin)),
            CreatePrincipal(
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Role, Roles.Admin),
                new Claim(ClaimTypes.Role, Roles.Client),
                new Claim(CustomClaimTypes.SecurityStamp, validStamp)),
            CreatePrincipal(
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Role, Roles.Client),
                new Claim(CustomClaimTypes.SecurityStamp, validStamp))
        };
    }

    private static CookiePrincipalValidationEvents CreateEvents(IUserRepository repository) =>
        new(
            new AccountPrincipalValidator(repository, SecurityStamps),
            new FixedTimeProvider(Now),
            NullLogger<CookiePrincipalValidationEvents>.Instance);

    private static CookieValidatePrincipalContext CreateContext(
        ClaimsPrincipal principal,
        AuthenticationProperties? properties = null,
        RecordingAuthenticationService? authentication = null)
    {
        authentication ??= new RecordingAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme,
            null,
            typeof(CookieAuthenticationHandler));
        var options = new CookieAuthenticationOptions();
        var ticket = new AuthenticationTicket(
            principal,
            properties ?? new AuthenticationProperties(),
            scheme.Name);
        return new CookieValidatePrincipalContext(httpContext, scheme, options, ticket);
    }

    private static ClaimsPrincipal CreateAdminPrincipal(AdminUser user) =>
        CreatePrincipal(
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Role, Roles.Admin),
            new Claim(
                CustomClaimTypes.SecurityStamp,
                SecurityStamps.Create(user.PasswordHash, user.PasswordSalt)));

    private static ClaimsPrincipal CreateClientPrincipal(ClientUser user) =>
        CreatePrincipal(
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Role, Roles.Client),
            new Claim(CustomClaimTypes.ClientId, user.ClientId.ToString()),
            new Claim(
                CustomClaimTypes.SecurityStamp,
                SecurityStamps.Create(user.PasswordHash, user.PasswordSalt)));

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

    private static AdminUser CreateAdminUser(
        bool status = true,
        DateTimeOffset? deactiveDate = null,
        string passwordHash = "password-hash",
        string passwordSalt = "password-salt") =>
        new()
        {
            Id = 7,
            Username = "admin",
            FullName = "Admin User",
            PasswordHash = passwordHash,
            PasswordSalt = passwordSalt,
            Status = status,
            DeactiveDate = deactiveDate
        };

    private static ClientUser CreateClientUser() =>
        new()
        {
            Id = 11,
            ClientId = 42,
            Username = "client",
            FullName = "Client User",
            PasswordHash = "password-hash",
            PasswordSalt = "password-salt",
            Status = true
        };

    private sealed class StubUserRepository : IUserRepository
    {
        public AdminUser? AdminById { get; init; }
        public ClientUser? ClientById { get; init; }
        public int AdminByIdCalls { get; private set; }
        public (long ClientId, long UserId)? LastClientLookup { get; private set; }

        public Task<AdminUser?> GetByUsernameAsync(string username) =>
            Task.FromResult<AdminUser?>(null);

        public Task<ClientUser?> GetClientUserAsync(long clientId, string username) =>
            Task.FromResult<ClientUser?>(null);

        public Task<AdminUser?> GetByIdAsync(
            long userId,
            CancellationToken cancellationToken = default)
        {
            AdminByIdCalls++;
            return Task.FromResult(AdminById);
        }

        public Task<ClientUser?> GetClientUserByIdAsync(
            long clientId,
            long userId,
            CancellationToken cancellationToken = default)
        {
            LastClientLookup = (clientId, userId);
            return Task.FromResult(ClientById is { ClientId: var storedClientId, Id: var storedUserId }
                && storedClientId == clientId
                && storedUserId == userId
                    ? ClientById
                    : null);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public string? SignOutScheme { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            SignOutScheme = scheme;
            return Task.CompletedTask;
        }
    }
}
