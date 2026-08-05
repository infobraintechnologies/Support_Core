using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Tests.Attachments;

public sealed class AttachmentServiceHealthTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateUploadIntent_UnhealthyCachedHealthRecovers_RechecksAndCreatesIntent()
    {
        var scanner = new SequencedScanner(
            new(false, Now.AddMinutes(-1), null, "clamav_definitions_stale"),
            new(true, Now, Now.AddHours(-6), null));
        var repository = new RecordingRepository(Now);
        var service = CreateService(scanner, repository);

        var result = await service.CreateUploadIntentAsync(
            123,
            new AttachmentActor(99, 42, false),
            new CreateAttachmentUploadRequest("report.pdf", "application/pdf", 1234));

        Assert.Equal(AttachmentCommandStatus.Accepted, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(1, scanner.HealthCheckCalls);
        Assert.Equal(1, repository.CreateIntentCalls);
    }

    [Fact]
    public async Task CreateUploadIntent_UnhealthyRecheck_BlocksWithScannerUnavailable()
    {
        var scanner = new SequencedScanner(
            new(false, Now.AddHours(-4), null, "clamav_unavailable"),
            new(false, Now, Now.AddHours(-25), "clamav_definitions_stale"));
        var repository = new RecordingRepository(Now);
        var service = CreateService(scanner, repository);

        var result = await service.CreateUploadIntentAsync(
            123,
            new AttachmentActor(99, 42, false),
            new CreateAttachmentUploadRequest("report.pdf", "application/pdf", 1234));

        Assert.Equal(AttachmentCommandStatus.ScannerUnavailable, result.Status);
        Assert.Equal("clamav_definitions_stale", result.ErrorCode);
        Assert.Equal(60, result.RetryAfterSeconds);
        Assert.Equal(1, scanner.HealthCheckCalls);
        Assert.Equal(0, repository.CreateIntentCalls);
    }

    [Fact]
    public async Task CreateUploadIntent_MalwareScanningWithoutScanner_FailsClosed()
    {
        var repository = new RecordingRepository(Now);
        var service = new AttachmentService(
            repository,
            new StubConversationService(),
            new StubStorage(),
            scanner: null,
            new AttachmentOptions
            {
                Enabled = true,
                SecurityMode = AttachmentSecurityMode.MalwareScanning
            },
            new FixedTimeProvider(Now));

        var result = await service.CreateUploadIntentAsync(
            123,
            new AttachmentActor(99, 42, false),
            new CreateAttachmentUploadRequest("report.pdf", "application/pdf", 1234));

        Assert.Equal(AttachmentCommandStatus.ScannerUnavailable, result.Status);
        Assert.Equal("malware_scanner_unavailable", result.ErrorCode);
        Assert.Equal(60, result.RetryAfterSeconds);
        Assert.Equal(0, repository.CreateIntentCalls);
    }

    [Theory]
    [InlineData(ConversationTypes.SupportGroup, InstructionCategories.Support)]
    [InlineData(ConversationTypes.SupportPrivate, InstructionCategories.Support)]
    [InlineData(ConversationTypes.TrainingTicket, InstructionCategories.Ticket)]
    [InlineData(ConversationTypes.AccountsInquiry, InstructionCategories.Inquiry)]
    public async Task StructuralValidationOnly_SupportedConversation_CreatesWithoutScanner(
        short instructionTypeId,
        short instructionCategoryId)
    {
        var repository = new RecordingRepository(Now);
        var service = CreateStructuralService(
            repository,
            new ConversationAccess(
                123,
                42,
                instructionTypeId,
                instructionCategoryId));

        var result = await service.CreateUploadIntentAsync(
            123,
            new AttachmentActor(99, 42, false),
            new CreateAttachmentUploadRequest("report.pdf", "application/pdf", 1234));

        Assert.Equal(AttachmentCommandStatus.Accepted, result.Status);
        Assert.Equal(1, repository.CreateIntentCalls);
    }

    [Fact]
    public async Task StructuralValidationOnly_InternalConversation_RemainsUnsupported()
    {
        var repository = new RecordingRepository(Now);
        var service = CreateStructuralService(
            repository,
            new ConversationAccess(
                123,
                42,
                ConversationTypes.InternalTeam,
                InstructionCategories.Support));

        var result = await service.CreateUploadIntentAsync(
            123,
            new AttachmentActor(99, 42, false),
            new CreateAttachmentUploadRequest("report.pdf", "application/pdf", 1234));

        Assert.Equal(AttachmentCommandStatus.Unavailable, result.Status);
        Assert.Equal("attachments_not_supported_for_conversation", result.ErrorCode);
        Assert.Equal(0, repository.CreateIntentCalls);
    }

    private static AttachmentService CreateService(
        SequencedScanner scanner,
        RecordingRepository repository) =>
        new(
            repository,
            new StubConversationService(),
            new StubStorage(),
            scanner,
            new AttachmentOptions
            {
                Enabled = true,
                SecurityMode = AttachmentSecurityMode.MalwareScanning
            },
            new FixedTimeProvider(Now));

    private static AttachmentService CreateStructuralService(
        RecordingRepository repository,
        ConversationAccess access) =>
        new(
            repository,
            new StubConversationService(access),
            new StubStorage(),
            scanner: null,
            new AttachmentOptions
            {
                Enabled = true,
                SecurityMode = AttachmentSecurityMode.StructuralValidationOnly
            },
            new FixedTimeProvider(Now));

    private sealed class SequencedScanner(
        FileScannerHealth cachedHealth,
        FileScannerHealth checkedHealth) : IFileScanner
    {
        public FileScannerHealth Health { get; } = cachedHealth;
        public int HealthCheckCalls { get; private set; }

        public Task<FileScannerHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default)
        {
            HealthCheckCalls++;
            return Task.FromResult(checkedHealth);
        }

        public Task<FileScanResult> ScanAsync(
            Stream content,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubConversationService(ConversationAccess? access = null)
        : IConversationService
    {
        public Task<ConversationAccess?> GetAccessAsync(
            long conversationId,
            ConversationActor actor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationAccess?>(access ?? new(
                    conversationId,
                    42,
                    ConversationTypes.SupportGroup,
                    100));

        public Task<ConversationMessage?> CreateMessageAsync(
            long conversationId,
            ConversationActor actor,
            string text,
            string? ipAddress,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubStorage : IFileStorage
    {
        public Task<string> CreatePresignedPutUrlAsync(
            string key,
            string mediaType,
            long size,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("https://storage.example.test/upload");

        public Task<string> CreatePresignedGetUrlAsync(
            string key,
            string disposition,
            string displayName,
            string mediaType,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredObjectInfo?> HeadAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredObjectRead?> OpenReadAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PromotionResult> PromoteAsync(
            string quarantineKey,
            string readyKey,
            string expectedSourceETag,
            ReadyObjectMetadata metadata,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteIfExistsAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRepository(DateTimeOffset now) : IAttachmentRepository
    {
        public int CreateIntentCalls { get; private set; }

        public Task<AttachmentCommandResult<AttachmentRecord>> CreateIntentAsync(
            AttachmentIntentRecord intent,
            AttachmentOptions options,
            CancellationToken cancellationToken = default)
        {
            CreateIntentCalls++;
            return Task.FromResult(
                new AttachmentCommandResult<AttachmentRecord>(
                    AttachmentCommandStatus.Accepted,
                    new AttachmentRecord(
                        intent.Id,
                        intent.ClientId,
                        intent.ConversationId,
                        null,
                        null,
                        null,
                        checked((int)intent.Actor.UserId),
                        AttachmentStates.PendingUpload,
                        intent.QuarantineKey,
                        null,
                        intent.DisplayName,
                        intent.DeclaredMediaType,
                        null,
                        intent.DeclaredSize,
                        null,
                        intent.DeclaredSize,
                        null,
                        null,
                        null,
                        now,
                        now,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        0,
                        now,
                        null,
                        null,
                        null,
                        0)));
        }

        public Task<AttachmentRecord?> GetAuthorizedAsync(
            Guid attachmentId,
            AttachmentActor actor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentRecord?> GetReadyForContentAsync(
            Guid attachmentId,
            AttachmentActor actor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentCommandResult<AttachmentRecord>> CompleteAsync(
            Guid attachmentId,
            AttachmentActor actor,
            long actualSize,
            string sourceETag,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentCommandResult<AttachmentRecord>> CancelAsync(
            Guid attachmentId,
            AttachmentActor actor,
            string rejectionCode,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IWorkerLeadershipLease?> TryAcquireWorkerLeadershipAsync(
            string workerName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AttachmentRecord>> ClaimScanBatchAsync(
            string leaseOwner,
            int batchSize,
            DateTimeOffset now,
            DateTimeOffset leaseUntil,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentRecord?> MarkPromotingAsync(
            Guid attachmentId,
            string leaseOwner,
            string detectedMediaType,
            long actualSize,
            string sourceETag,
            byte[] sha256,
            string readyKey,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> MarkReadyAsync(
            Guid attachmentId,
            string leaseOwner,
            string readyETag,
            DateTimeOffset now,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task MarkRejectedForDeleteAsync(
            Guid attachmentId,
            string leaseOwner,
            string rejectionCode,
            string targetState,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReleaseScanForRetryAsync(
            Guid attachmentId,
            string leaseOwner,
            DateTimeOffset nextAttemptAt,
            string errorCode,
            bool consumeAttempt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReleaseDeletionForRetryAsync(
            Guid attachmentId,
            string leaseOwner,
            DateTimeOffset nextAttemptAt,
            string errorCode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AttachmentRecord>> ClaimCleanupBatchAsync(
            string leaseOwner,
            int batchSize,
            DateTimeOffset now,
            DateTimeOffset leaseUntil,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AttachmentRecord>> ClaimReadyQuarantineCleanupBatchAsync(
            string leaseOwner,
            int batchSize,
            DateTimeOffset now,
            DateTimeOffset leaseUntil,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> CompleteReadyQuarantineCleanupAsync(
            Guid attachmentId,
            string leaseOwner,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReleaseReadyQuarantineCleanupForRetryAsync(
            Guid attachmentId,
            string leaseOwner,
            DateTimeOffset nextAttemptAt,
            string errorCode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task FinalizeDeletionAsync(
            Guid attachmentId,
            string leaseOwner,
            string targetState,
            string? rejectionCode,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
