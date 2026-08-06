using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace CBSSupport.Shared.Services;

public sealed class R2FileStorage : IFileStorage, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    public R2FileStorage(R2StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _bucket = options.BucketName;
        var serviceUrl = options.ServiceUrl?.TrimEnd('/')
            ?? $"https://{options.AccountId}.r2.cloudflarestorage.com";
        var configuration = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "auto"
        };
        _client = new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            configuration);
    }

    internal R2FileStorage(IAmazonS3 client, string bucket)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new ArgumentException("Bucket is required.", nameof(bucket));
        }
        _client = client;
        _bucket = bucket;
    }

    public Task<string> CreatePresignedPutUrlAsync(
        string key,
        string mediaType,
        long size,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = mediaType,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = Protocol.HTTPS
        };
        return _client.GetPreSignedURLAsync(request);
    }

    public Task<string> CreatePresignedGetUrlAsync(
        string key,
        string disposition,
        string displayName,
        string mediaType,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeDisposition = string.Equals(
            disposition,
            "inline",
            StringComparison.OrdinalIgnoreCase) ? "inline" : "attachment";
        var asciiName = string.Concat(displayName.Select(character =>
            character is >= ' ' and <= '~' && character is not '"' and not '\\'
                ? character
                : '_'));
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = Protocol.HTTPS,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentType = mediaType,
                ContentDisposition =
                    $"{safeDisposition}; filename=\"{asciiName}\"; filename*=UTF-8''{Uri.EscapeDataString(displayName)}"
            }
        };
        return _client.GetPreSignedURLAsync(request);
    }

    public async Task<StoredObjectInfo?> HeadAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = _bucket,
                    Key = key
                },
                cancellationToken);
            return ToInfo(key, response.ContentLength, response.ETag, response.Headers.ContentType, response.Metadata);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<StoredObjectRead?> OpenReadAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = key },
                cancellationToken);
            var info = ToInfo(
                key,
                response.ContentLength,
                response.ETag,
                response.Headers.ContentType,
                response.Metadata);
            return new StoredObjectRead(info, new ResponseStream(response));
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<PromotionResult> PromoteAsync(
        string quarantineKey,
        string readyKey,
        string expectedSourceETag,
        ReadyObjectMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var existing = await HeadAsync(readyKey, cancellationToken);
        if (existing is not null)
        {
            return Matches(existing, metadata)
                ? PromotionResult.ExistingExactMatch
                : PromotionResult.ReadyConflict;
        }

        var source = await HeadAsync(quarantineKey, cancellationToken);
        if (source is null)
        {
            return PromotionResult.MissingSource;
        }
        if (!ETagEquals(source.ETag, expectedSourceETag))
        {
            return PromotionResult.SourceChanged;
        }

        var request = new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = quarantineKey,
            DestinationBucket = _bucket,
            DestinationKey = readyKey,
            ETagToMatch = QuoteETag(expectedSourceETag),
            IfNoneMatch = "*",
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = metadata.MediaType
        };
        request.Metadata["attachment-id"] = metadata.AttachmentId.ToString("D");
        request.Metadata["source-etag"] = NormalizeETag(metadata.SourceETag);
        request.Metadata["sha256"] = metadata.Sha256;
        request.Metadata["media-type"] = metadata.MediaType;
        request.Metadata["size"] = metadata.Size.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await _client.CopyObjectAsync(request, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is HttpStatusCode.PreconditionFailed
                or HttpStatusCode.Conflict)
        {
            var racedReady = await HeadAsync(readyKey, cancellationToken);
            if (racedReady is not null)
            {
                return Matches(racedReady, metadata)
                    ? PromotionResult.ExistingExactMatch
                    : PromotionResult.ReadyConflict;
            }
            var currentSource = await HeadAsync(quarantineKey, cancellationToken);
            if (currentSource is null)
            {
                return PromotionResult.MissingSource;
            }
            return ETagEquals(currentSource.ETag, expectedSourceETag)
                ? PromotionResult.RetryableConflict
                : PromotionResult.SourceChanged;
        }

        var copied = await HeadAsync(readyKey, cancellationToken);
        return copied is not null && Matches(copied, metadata)
            ? PromotionResult.Copied
            : PromotionResult.ReadyConflict;
    }

    public async Task<ValidatedWriteResult> StoreValidatedAsync(
        string quarantineKey,
        string readyKey,
        string expectedSourceETag,
        ReadyObjectMetadata metadata,
        Stream validatedContent,
        CancellationToken cancellationToken = default)
    {
        var existing = await HeadAsync(readyKey, cancellationToken);
        if (existing is not null)
        {
            return Matches(existing, metadata)
                ? ValidatedWriteResult.ExistingExactMatch
                : ValidatedWriteResult.ReadyConflict;
        }
        var source = await HeadAsync(quarantineKey, cancellationToken);
        if (source is null || !ETagEquals(source.ETag, expectedSourceETag))
        {
            return ValidatedWriteResult.SourceChanged;
        }

        if (validatedContent.CanSeek)
        {
            validatedContent.Position = 0;
        }
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = readyKey,
            InputStream = validatedContent,
            AutoCloseStream = false,
            ContentType = metadata.MediaType,
            IfNoneMatch = "*",
            // R2 does not implement the streaming SigV4 trailer emitted by
            // AWSSDK.S3. HTTPS plus the post-write metadata/hash verification
            // below retain transport and application-level integrity checks.
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true
        };
        request.Metadata["attachment-id"] = metadata.AttachmentId.ToString("D");
        request.Metadata["source-etag"] = NormalizeETag(metadata.SourceETag);
        request.Metadata["sha256"] = metadata.Sha256;
        request.Metadata["media-type"] = metadata.MediaType;
        request.Metadata["size"] = metadata.Size.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await _client.PutObjectAsync(request, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is HttpStatusCode.PreconditionFailed
                or HttpStatusCode.Conflict)
        {
            var racedReady = await HeadAsync(readyKey, cancellationToken);
            if (racedReady is not null)
            {
                return Matches(racedReady, metadata)
                    ? ValidatedWriteResult.ExistingExactMatch
                    : ValidatedWriteResult.ReadyConflict;
            }
            return ValidatedWriteResult.RetryableConflict;
        }
        var written = await HeadAsync(readyKey, cancellationToken);
        return written is not null && Matches(written, metadata)
            ? ValidatedWriteResult.Written
            : ValidatedWriteResult.ReadyConflict;
    }

    public async Task DeleteIfExistsAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = _bucket, Key = key },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is HttpStatusCode.NotFound)
        {
            // Idempotent deletion: an R2 404 is already the desired physical state.
        }
    }

    public void Dispose() => _client.Dispose();

    private static StoredObjectInfo ToInfo(
        string key,
        long size,
        string etag,
        string? mediaType,
        MetadataCollection metadata)
    {
        var values = metadata.Keys.ToDictionary(
            keyName => keyName.Trim().ToLowerInvariant(),
            keyName => metadata[keyName],
            StringComparer.OrdinalIgnoreCase);
        return new StoredObjectInfo(
            key,
            size,
            NormalizeETag(etag),
            mediaType,
            values);
    }

    private static bool Matches(StoredObjectInfo value, ReadyObjectMetadata expected) =>
        value.Size == expected.Size
        && string.Equals(value.ContentType, expected.MediaType, StringComparison.OrdinalIgnoreCase)
        && Metadata(value, "attachment-id") == expected.AttachmentId.ToString("D")
        && ETagEquals(Metadata(value, "source-etag"), expected.SourceETag)
        && string.Equals(Metadata(value, "sha256"), expected.Sha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Metadata(value, "media-type"), expected.MediaType, StringComparison.OrdinalIgnoreCase)
        && Metadata(value, "size") == expected.Size.ToString(
            System.Globalization.CultureInfo.InvariantCulture);

    private static string? Metadata(StoredObjectInfo value, string key) =>
        value.Metadata.GetValueOrDefault(key)
        ?? value.Metadata.GetValueOrDefault($"x-amz-meta-{key}");

    private static bool ETagEquals(string? left, string? right) =>
        string.Equals(NormalizeETag(left), NormalizeETag(right), StringComparison.Ordinal);

    private static string NormalizeETag(string? etag) =>
        (etag ?? string.Empty).Trim().Trim('"');

    private static string QuoteETag(string etag) => $"\"{NormalizeETag(etag)}\"";

    private sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                response.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            response.Dispose();
            await base.DisposeAsync();
        }
    }
}
