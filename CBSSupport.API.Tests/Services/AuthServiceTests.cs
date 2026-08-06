using CBSSupport.Shared.Data;
using CBSSupport.Shared.Helpers;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Tests.Services;

public sealed class AuthServiceTests
{
    private const string Password = "correct horse battery staple";
    private const string Pepper = "test-company-pepper";
    private static readonly Lazy<(string Hash, string Salt)> Credentials = new(CreateCredentials);

    [Fact]
    public async Task ValidateUserAsync_ActiveAccountWithValidPassword_ReturnsUser()
    {
        var user = CreateAdminUser(status: true, deactiveDate: null);
        var service = CreateService(new StubUserRepository { AdminByUsername = user });

        var result = await service.ValidateUserAsync(user.Username, Password);

        Assert.Same(user, result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ValidateUserAsync_InactiveAccountWithValidPassword_ReturnsNull(
        bool status,
        bool hasDeactiveDate)
    {
        var user = CreateAdminUser(
            status,
            hasDeactiveDate ? DateTimeOffset.UtcNow : null);
        var service = CreateService(new StubUserRepository { AdminByUsername = user });

        var result = await service.ValidateUserAsync(user.Username, Password);

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateClientUserAsync_ActiveAccountWithValidPassword_ReturnsUser()
    {
        var user = CreateClientUser(status: true, deactiveDate: null);
        var service = CreateService(new StubUserRepository { ClientByUsername = user });

        var result = await service.ValidateClientUserAsync(user.ClientId, user.Username, Password);

        Assert.Same(user, result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ValidateClientUserAsync_InactiveAccountWithValidPassword_ReturnsNull(
        bool status,
        bool hasDeactiveDate)
    {
        var user = CreateClientUser(
            status,
            hasDeactiveDate ? DateTimeOffset.UtcNow : null);
        var service = CreateService(new StubUserRepository { ClientByUsername = user });

        var result = await service.ValidateClientUserAsync(user.ClientId, user.Username, Password);

        Assert.Null(result);
    }

    private static AdminUser CreateAdminUser(bool status, DateTimeOffset? deactiveDate)
    {
        var credentials = Credentials.Value;
        return new AdminUser
        {
            Id = 7,
            Username = "admin",
            FullName = "Admin User",
            PasswordHash = credentials.Hash,
            PasswordSalt = credentials.Salt,
            Status = status,
            DeactiveDate = deactiveDate
        };
    }

    private static ClientUser CreateClientUser(bool status, DateTimeOffset? deactiveDate)
    {
        var credentials = Credentials.Value;
        return new ClientUser
        {
            Id = 11,
            ClientId = 42,
            Username = "client",
            FullName = "Client User",
            Role = "Client",
            PasswordHash = credentials.Hash,
            PasswordSalt = credentials.Salt,
            Status = status,
            DeactiveDate = deactiveDate
        };
    }

    private static (string Hash, string Salt) CreateCredentials()
    {
        var (hash, salt) = PasswordHelper.HashPassword(Password, Pepper);
        return (hash, salt);
    }

    private static AuthService CreateService(StubUserRepository repository) =>
        new(repository, new PasswordHashOptions { Pepper = Pepper });

    private sealed class StubUserRepository : IUserRepository
    {
        public AdminUser? AdminByUsername { get; init; }
        public ClientUser? ClientByUsername { get; init; }

        public Task<AdminUser?> GetByUsernameAsync(string username) =>
            Task.FromResult(AdminByUsername);

        public Task<ClientUser?> GetClientUserAsync(long clientId, string username) =>
            Task.FromResult(ClientByUsername);

        public Task<AdminUser?> GetByIdAsync(
            long userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AdminUser?>(null);

        public Task<ClientUser?> GetClientUserByIdAsync(
            long clientId,
            long userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ClientUser?>(null);
    }
}
