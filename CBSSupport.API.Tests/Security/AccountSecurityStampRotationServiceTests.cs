using CBSSupport.Shared.Data;
using CBSSupport.API.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Security;

public sealed class AccountSecurityStampRotationServiceTests
{
    [Fact]
    public async Task PasswordChange_RotatesWithExpectedStampAndDoesNotExposeStamp()
    {
        var store = new RecordingStore();
        var stamps = new DataProtectionAccountSecurityStampService(
            new EphemeralDataProtectionProvider());
        var service = new AccountSecurityStampRotationService(
            store,
            stamps,
            new LocalHubConnectionRevocationNotifier(new ActiveHubConnectionRegistry()),
            NullLogger<AccountSecurityStampRotationService>.Instance);
        var currentStamp = Enumerable.Repeat((byte)3, 32).ToArray();
        var account = new AccountReference(AccountKind.Client, 11);

        var rotated = await service.RotateForPasswordChangeAsync(account, currentStamp);

        Assert.True(rotated);
        Assert.Equal(account, store.Account);
        Assert.Equal(currentStamp, store.ExpectedStamp);
        Assert.NotNull(store.ReplacementStamp);
        Assert.Equal(32, store.ReplacementStamp!.Length);
        Assert.False(store.ReplacementStamp.SequenceEqual(currentStamp));
    }

    [Fact]
    public async Task ConcurrentRotations_ProduceDistinctRandomStampsAndSerializeAtStoreBoundary()
    {
        var store = new RecordingStore();
        var service = new AccountSecurityStampRotationService(
            store,
            new DataProtectionAccountSecurityStampService(new EphemeralDataProtectionProvider()),
            new LocalHubConnectionRevocationNotifier(new ActiveHubConnectionRegistry()),
            NullLogger<AccountSecurityStampRotationService>.Instance);
        var account = new AccountReference(AccountKind.Administrator, 7);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            service.RevokeAllSessionsAsync(account)));

        Assert.Equal(8, store.ReplacementStamps.Count);
        Assert.Equal(
            8,
            store.ReplacementStamps
                .Select(Convert.ToBase64String)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(store.ReplacementStamps, stamp => Assert.Equal(32, stamp.Length));
    }

    private sealed class RecordingStore : IAccountSecurityStampStore
    {
        private readonly object _sync = new();

        public AccountReference? Account { get; private set; }
        public byte[]? ExpectedStamp { get; private set; }
        public byte[]? ReplacementStamp { get; private set; }
        public List<byte[]> ReplacementStamps { get; } = [];
        public Task<bool> RotateAsync(
            AccountReference account,
            byte[] replacementStamp,
            byte[]? expectedStamp = null,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Account = account;
                ExpectedStamp = expectedStamp?.ToArray();
                ReplacementStamp = replacementStamp.ToArray();
                ReplacementStamps.Add(replacementStamp.ToArray());
            }

            return Task.FromResult(true);
        }
    }
}
