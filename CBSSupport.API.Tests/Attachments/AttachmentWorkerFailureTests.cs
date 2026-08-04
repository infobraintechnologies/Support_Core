using CBSSupport.API.Attachments;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Attachments;

public sealed class AttachmentWorkerFailureTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Promotion_CopySucceededButFinalizationFailed_RetryFinalizesExactReady()
    {
        var repository = new RecordingRepository
        {
            MarkReadyResults = new Queue<Task<bool>>(
            [
                Task.FromException<bool>(new InvalidOperationException("database unavailable")),
                Task.FromResult(true)
            ])
        };
        var storage = new RecordingStorage
        {
            PromotionResults = new Queue<PromotionResult>(
            [
                PromotionResult.Copied,
                PromotionResult.ExistingExactMatch
            ])
        };
        var worker = CreateScanWorker(repository, storage);
        var attachment = Record(AttachmentStates.Promoting);

        await worker.ProcessOnceAsync(attachment);
        await worker.ProcessOnceAsync(attachment with { AttemptCount = 1 });

        Assert.Equal(2, storage.PromoteCalls);
        Assert.Equal(2, repository.MarkReadyCalls);
        Assert.Equal(1, repository.ScanRetryCalls);
        Assert.Equal(1, storage.DeleteCalls);
        Assert.Equal(1, repository.QuarantineCleanupCalls);
    }

    [Fact]
    public async Task Promotion_CancelWonRace_DoesNotDeleteObjectsAfterFailedFinalization()
    {
        var repository = new RecordingRepository
        {
            MarkReadyResults = new Queue<Task<bool>>([Task.FromResult(false)])
        };
        var storage = new RecordingStorage
        {
            PromotionResults = new Queue<PromotionResult>([PromotionResult.Copied])
        };
        var worker = CreateScanWorker(repository, storage);

        await worker.ProcessOnceAsync(Record(AttachmentStates.Promoting));

        Assert.Equal(1, repository.MarkReadyCalls);
        Assert.Equal(0, storage.DeleteCalls);
        Assert.Equal(0, repository.QuarantineCleanupCalls);
    }

    [Fact]
    public async Task Scan_ChangedQuarantineETag_RejectsWithoutScanningOrPromotion()
    {
        var repository = new RecordingRepository();
        var storage = new RecordingStorage
        {
            OpenReadResult = new StoredObjectRead(
                new StoredObjectInfo(
                    "quarantine/key",
                    1234,
                    "changed-etag",
                    "application/pdf",
                    new Dictionary<string, string>()),
                new MemoryStream("%PDF-test"u8.ToArray()))
        };
        var scanner = new RecordingScanner();
        var worker = CreateScanWorker(repository, storage, scanner);

        await worker.ProcessOnceAsync(Record(AttachmentStates.Scanning));

        Assert.Equal(AttachmentRejectionCodes.ObjectChangedAfterComplete, repository.RejectionCode);
        Assert.Equal(AttachmentStates.Rejected, repository.RejectionTargetState);
        Assert.Equal(0, scanner.ScanCalls);
        Assert.Equal(0, storage.PromoteCalls);
    }

    [Fact]
    public async Task Promotion_ReadyMismatch_FailsClosedWithStableCode()
    {
        var repository = new RecordingRepository();
        var storage = new RecordingStorage
        {
            PromotionResults = new Queue<PromotionResult>([PromotionResult.ReadyConflict])
        };
        var worker = CreateScanWorker(repository, storage);

        await worker.ProcessOnceAsync(Record(AttachmentStates.Promoting));

        Assert.Equal(AttachmentRejectionCodes.ReadyObjectConflict, repository.RejectionCode);
        Assert.Equal(AttachmentStates.ScanFailed, repository.RejectionTargetState);
    }

    [Fact]
    public async Task Cleanup_DeleteSucceededButFinalizationFailed_RetryFinalizesAfterIdempotentDeletes()
    {
        var repository = new RecordingRepository
        {
            FinalizeResults = new Queue<Task>(
            [
                Task.FromException(new InvalidOperationException("database unavailable")),
                Task.CompletedTask
            ])
        };
        var storage = new RecordingStorage();
        var worker = new AttachmentCleanupWorker(
            repository,
            storage,
            new FixedTimeProvider(Now),
            NullLogger<AttachmentCleanupWorker>.Instance);
        var attachment = Record(AttachmentStates.DeletePending) with
        {
            DeleteTargetState = AttachmentStates.Rejected,
            RejectionCode = AttachmentRejectionCodes.MalwareDetected
        };

        await worker.ProcessDeletionOnceAsync(attachment);
        await worker.ProcessDeletionOnceAsync(attachment);

        Assert.Equal(4, storage.DeleteCalls);
        Assert.Equal(2, repository.FinalizeCalls);
        Assert.Equal(1, repository.DeletionRetryCalls);
        Assert.All(
            repository.FinalizedRejectionCodes,
            code => Assert.Equal(AttachmentRejectionCodes.MalwareDetected, code));
    }

    private static AttachmentScanWorker CreateScanWorker(
        RecordingRepository repository,
        RecordingStorage storage,
        RecordingScanner? scanner = null) =>
        new(
            repository,
            storage,
            scanner ?? new RecordingScanner(),
            new AttachmentOptions(),
            new FixedTimeProvider(Now),
            NullLogger<AttachmentScanWorker>.Instance);

    private static AttachmentRecord Record(string state) =>
        new(
            Guid.NewGuid(),
            42,
            123,
            null,
            null,
            null,
            99,
            state,
            "quarantine/key",
            "ready/key",
            "document.pdf",
            "application/pdf",
            "application/pdf",
            1234,
            1234,
            1234,
            "source-etag",
            "source-etag",
            new byte[32],
            Now.AddMinutes(-5),
            Now,
            Now.AddMinutes(-4),
            null,
            null,
            null,
            null,
            "worker",
            Now.AddMinutes(3),
            0,
            Now,
            null,
            state == AttachmentStates.DeletePending
                ? AttachmentStates.Deleted
                : null,
            null,
            0);

    private sealed class RecordingRepository : AttachmentRepositoryStub
    {
        public Queue<Task<bool>> MarkReadyResults { get; init; } = [];
        public Queue<Task> FinalizeResults { get; init; } = [];
        public int MarkReadyCalls { get; private set; }
        public int ScanRetryCalls { get; private set; }
        public int QuarantineCleanupCalls { get; private set; }
        public int DeletionRetryCalls { get; private set; }
        public int FinalizeCalls { get; private set; }
        public string? RejectionCode { get; private set; }
        public string? RejectionTargetState { get; private set; }
        public List<string?> FinalizedRejectionCodes { get; } = [];

        public override Task<bool> MarkReadyAsync(
            Guid attachmentId,
            string leaseOwner,
            string readyETag,
            DateTimeOffset now,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
        {
            MarkReadyCalls++;
            return MarkReadyResults.Count > 0
                ? MarkReadyResults.Dequeue()
                : Task.FromResult(true);
        }

        public override Task ReleaseScanForRetryAsync(
            Guid attachmentId,
            string leaseOwner,
            DateTimeOffset nextAttemptAt,
            string errorCode,
            bool consumeAttempt,
            CancellationToken cancellationToken = default)
        {
            ScanRetryCalls++;
            return Task.CompletedTask;
        }

        public override Task MarkRejectedForDeleteAsync(
            Guid attachmentId,
            string leaseOwner,
            string rejectionCode,
            string targetState,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            RejectionCode = rejectionCode;
            RejectionTargetState = targetState;
            return Task.CompletedTask;
        }

        public override Task<bool> CompleteReadyQuarantineCleanupAsync(
            Guid attachmentId,
            string leaseOwner,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            QuarantineCleanupCalls++;
            return Task.FromResult(true);
        }

        public override Task FinalizeDeletionAsync(
            Guid attachmentId,
            string leaseOwner,
            string targetState,
            string? rejectionCode,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            FinalizeCalls++;
            FinalizedRejectionCodes.Add(rejectionCode);
            return FinalizeResults.Count > 0
                ? FinalizeResults.Dequeue()
                : Task.CompletedTask;
        }

        public override Task ReleaseDeletionForRetryAsync(
            Guid attachmentId,
            string leaseOwner,
            DateTimeOffset nextAttemptAt,
            string errorCode,
            CancellationToken cancellationToken = default)
        {
            DeletionRetryCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStorage : IFileStorage
    {
        public Queue<PromotionResult> PromotionResults { get; init; } = [];
        public StoredObjectRead? OpenReadResult { get; init; }
        public int PromoteCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public Task<PromotionResult> PromoteAsync(
            string quarantineKey,
            string readyKey,
            string expectedSourceETag,
            ReadyObjectMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            PromoteCalls++;
            return Task.FromResult(
                PromotionResults.Count > 0
                    ? PromotionResults.Dequeue()
                    : PromotionResult.Copied);
        }

        public Task<StoredObjectInfo?> HeadAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredObjectInfo?>(
                new(
                    key,
                    1234,
                    "ready-etag",
                    "application/pdf",
                    new Dictionary<string, string>()));

        public Task<StoredObjectRead?> OpenReadAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OpenReadResult);

        public Task DeleteIfExistsAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }

        public Task<string> CreatePresignedPutUrlAsync(
            string key,
            string mediaType,
            long size,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> CreatePresignedGetUrlAsync(
            string key,
            string disposition,
            string displayName,
            string mediaType,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingScanner : IFileScanner
    {
        public int ScanCalls { get; private set; }
        public FileScannerHealth Health { get; } =
            new(true, Now, Now, null);

        public Task<FileScannerHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Health);

        public Task<FileScanResult> ScanAsync(
            Stream content,
            CancellationToken cancellationToken = default)
        {
            ScanCalls++;
            return Task.FromResult(new FileScanResult(FileScanStatus.Clean));
        }
    }

    private abstract class AttachmentRepositoryStub : IAttachmentRepository
    {
        public virtual Task<AttachmentCommandResult<AttachmentRecord>> CreateIntentAsync(
            AttachmentIntentRecord intent,
            AttachmentOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task<AttachmentRecord?> GetAuthorizedAsync(
            Guid attachmentId,
            AttachmentActor actor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task<AttachmentRecord?> GetReadyForContentAsync(
            Guid attachmentId,
            AttachmentActor actor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task<AttachmentCommandResult<AttachmentRecord>> CompleteAsync(
            Guid attachmentId,
            AttachmentActor actor,
            long actualSize,
            string sourceETag,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task<AttachmentCommandResult<AttachmentRecord>> CancelAsync(
            Guid attachmentId,
            AttachmentActor actor,
            string rejectionCode,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task<IWorkerLeadershipLease?> TryAcquireWorkerLeadershipAsync(
            string workerName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task<IReadOnlyList<AttachmentRecord>> ClaimScanBatchAsync(
            string leaseOwner,
            int batchSize,
            DateTimeOffset now,
            DateTimeOffset leaseUntil,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task<AttachmentRecord?> MarkPromotingAsync(
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
        public virtual Task<bool> MarkReadyAsync(
            Guid attachmentId,
            string leaseOwner,
            string readyETag,
            DateTimeOffset now,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task MarkRejectedForDeleteAsync(
            Guid attachmentId,
            string leaseOwner,
            string rejectionCode,
            string targetState,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task ReleaseScanForRetryAsync(
            Guid attachmentId,
            string leaseOwner,
            DateTimeOffset nextAttemptAt,
            string errorCode,
            bool consumeAttempt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task ReleaseDeletionForRetryAsync(
            Guid attachmentId,
            string leaseOwner,
            DateTimeOffset nextAttemptAt,
            string errorCode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task<IReadOnlyList<AttachmentRecord>> ClaimCleanupBatchAsync(
            string leaseOwner,
            int batchSize,
            DateTimeOffset now,
            DateTimeOffset leaseUntil,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task<IReadOnlyList<AttachmentRecord>> ClaimReadyQuarantineCleanupBatchAsync(
            string leaseOwner,
            int batchSize,
            DateTimeOffset now,
            DateTimeOffset leaseUntil,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task<bool> CompleteReadyQuarantineCleanupAsync(
            Guid attachmentId,
            string leaseOwner,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task ReleaseReadyQuarantineCleanupForRetryAsync(
            Guid attachmentId,
            string leaseOwner,
            DateTimeOffset nextAttemptAt,
            string errorCode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public virtual Task FinalizeDeletionAsync(
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
