using System.Net;
using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Tests.Attachments;

public sealed class R2FileStorageTests
{
    [Fact]
    public async Task PresignedRequests_UseApprovedLifetimesAndHeaders()
    {
        GetPreSignedUrlRequest? put = null;
        GetPreSignedUrlRequest? get = null;
        var client = CreateClient((method, arguments) =>
        {
            if (method.Name == nameof(IAmazonS3.GetPreSignedURLAsync))
            {
                var request = Assert.IsType<GetPreSignedUrlRequest>(arguments[0]);
                if (request.Verb == HttpVerb.PUT)
                {
                    put = request;
                    return Task.FromResult("https://upload.invalid/signed");
                }
                get = request;
                return Task.FromResult("https://download.invalid/signed");
            }
            return Default(method.ReturnType);
        });
        using var storage = new R2FileStorage(client, "private-bucket");
        var before = DateTime.UtcNow;

        var putUrl = await storage.CreatePresignedPutUrlAsync(
            "quarantine/42/id",
            "application/pdf",
            1234,
            TimeSpan.FromMinutes(5));
        var getUrl = await storage.CreatePresignedGetUrlAsync(
            "ready/42/id",
            "attachment",
            "safe.pdf",
            "application/pdf",
            TimeSpan.FromSeconds(60));

        Assert.Equal("https://upload.invalid/signed", putUrl);
        Assert.Equal("https://download.invalid/signed", getUrl);
        Assert.NotNull(put);
        Assert.Equal("private-bucket", put.BucketName);
        Assert.Equal("quarantine/42/id", put.Key);
        Assert.Equal("application/pdf", put.ContentType);
        Assert.True(put.Expires.HasValue);
        Assert.InRange(
            put.Expires.Value,
            before.AddMinutes(5),
            DateTime.UtcNow.AddMinutes(5));
        Assert.NotNull(get);
        Assert.Equal("ready/42/id", get.Key);
        Assert.True(get.Expires.HasValue);
        Assert.InRange(
            get.Expires.Value,
            before.AddSeconds(60),
            DateTime.UtcNow.AddSeconds(60));
        Assert.Equal("application/pdf", get.ResponseHeaderOverrides.ContentType);
        Assert.Contains("safe.pdf", get.ResponseHeaderOverrides.ContentDisposition);
    }

    [Fact]
    public async Task Promote_NewReadyObject_UsesBothSourceAndDestinationConditions()
    {
        var attachmentId = Guid.NewGuid();
        var metadata = ReadyMetadata(attachmentId);
        CopyObjectRequest? copy = null;
        var readyExists = false;
        var client = CreateClient((method, arguments) =>
        {
            if (method.Name == nameof(IAmazonS3.GetObjectMetadataAsync))
            {
                var request = Assert.IsType<GetObjectMetadataRequest>(arguments[0]);
                if (request.Key == "ready/key" && !readyExists)
                {
                    return NotFound<GetObjectMetadataResponse>();
                }
                return Task.FromResult(
                    request.Key == "ready/key"
                        ? ObjectResponse("ready-etag", metadata)
                        : ObjectResponse("source-etag", null));
            }
            if (method.Name == nameof(IAmazonS3.CopyObjectAsync))
            {
                copy = Assert.IsType<CopyObjectRequest>(arguments[0]);
                readyExists = true;
                return Task.FromResult(new CopyObjectResponse());
            }
            return Default(method.ReturnType);
        });
        using var storage = new R2FileStorage(client, "private-bucket");

        var result = await storage.PromoteAsync(
            "quarantine/key",
            "ready/key",
            "source-etag",
            metadata);

        Assert.Equal(PromotionResult.Copied, result);
        Assert.NotNull(copy);
        Assert.Equal("\"source-etag\"", copy.ETagToMatch);
        Assert.Equal("*", copy.IfNoneMatch);
        Assert.Equal(S3MetadataDirective.REPLACE, copy.MetadataDirective);
        Assert.Equal(attachmentId.ToString("D"), copy.Metadata["attachment-id"]);
        Assert.Equal(metadata.Sha256, copy.Metadata["sha256"]);
    }

    [Fact]
    public async Task Promote_ExistingExactReady_IsNoOp()
    {
        var metadata = ReadyMetadata(Guid.NewGuid());
        var copyCalls = 0;
        var client = CreateClient((method, arguments) =>
        {
            if (method.Name == nameof(IAmazonS3.GetObjectMetadataAsync))
            {
                return Task.FromResult(ObjectResponse("ready-etag", metadata));
            }
            if (method.Name == nameof(IAmazonS3.CopyObjectAsync))
            {
                copyCalls++;
                return Task.FromResult(new CopyObjectResponse());
            }
            return Default(method.ReturnType);
        });
        using var storage = new R2FileStorage(client, "private-bucket");

        var result = await storage.PromoteAsync(
            "quarantine/key",
            "ready/key",
            "source-etag",
            metadata);

        Assert.Equal(PromotionResult.ExistingExactMatch, result);
        Assert.Equal(0, copyCalls);
    }

    [Fact]
    public async Task Promote_ExistingMetadataWithWrongLength_FailsClosed()
    {
        var metadata = ReadyMetadata(Guid.NewGuid());
        var response = ObjectResponse("ready-etag", metadata);
        response.ContentLength++;
        var client = CreateClient((method, _) =>
            method.Name == nameof(IAmazonS3.GetObjectMetadataAsync)
                ? Task.FromResult(response)
                : Default(method.ReturnType));
        using var storage = new R2FileStorage(client, "private-bucket");

        var result = await storage.PromoteAsync(
            "quarantine/key",
            "ready/key",
            "source-etag",
            metadata);

        Assert.Equal(PromotionResult.ReadyConflict, result);
    }

    [Fact]
    public async Task Promote_ConditionalDestinationRaceWithoutObject_IsRetryable()
    {
        var metadata = ReadyMetadata(Guid.NewGuid());
        var client = CreateClient((method, arguments) =>
        {
            if (method.Name == nameof(IAmazonS3.GetObjectMetadataAsync))
            {
                var request = Assert.IsType<GetObjectMetadataRequest>(arguments[0]);
                return request.Key == "ready/key"
                    ? NotFound<GetObjectMetadataResponse>()
                    : Task.FromResult(ObjectResponse("source-etag", null));
            }
            if (method.Name == nameof(IAmazonS3.CopyObjectAsync))
            {
                throw new AmazonS3Exception("destination race")
                {
                    StatusCode = HttpStatusCode.Conflict
                };
            }
            return Default(method.ReturnType);
        });
        using var storage = new R2FileStorage(client, "private-bucket");

        var result = await storage.PromoteAsync(
            "quarantine/key",
            "ready/key",
            "source-etag",
            metadata);

        Assert.Equal(PromotionResult.RetryableConflict, result);
    }

    [Fact]
    public async Task Promote_ChangedSourceETag_IsRejectedBeforeCopy()
    {
        var metadata = ReadyMetadata(Guid.NewGuid());
        var client = CreateClient((method, arguments) =>
        {
            if (method.Name == nameof(IAmazonS3.GetObjectMetadataAsync))
            {
                var request = Assert.IsType<GetObjectMetadataRequest>(arguments[0]);
                return request.Key == "ready/key"
                    ? NotFound<GetObjectMetadataResponse>()
                    : Task.FromResult(ObjectResponse("changed-etag", null));
            }
            return Default(method.ReturnType);
        });
        using var storage = new R2FileStorage(client, "private-bucket");

        var result = await storage.PromoteAsync(
            "quarantine/key",
            "ready/key",
            "source-etag",
            metadata);

        Assert.Equal(PromotionResult.SourceChanged, result);
    }

    [Fact]
    public async Task Promote_SourceChangedDuringConditionalCopy_IsRejected()
    {
        var metadata = ReadyMetadata(Guid.NewGuid());
        var sourceHeads = 0;
        var client = CreateClient((method, arguments) =>
        {
            if (method.Name == nameof(IAmazonS3.GetObjectMetadataAsync))
            {
                var request = Assert.IsType<GetObjectMetadataRequest>(arguments[0]);
                if (request.Key == "ready/key")
                {
                    return NotFound<GetObjectMetadataResponse>();
                }
                sourceHeads++;
                return Task.FromResult(ObjectResponse(
                    sourceHeads == 1 ? "source-etag" : "changed-etag",
                    null));
            }
            if (method.Name == nameof(IAmazonS3.CopyObjectAsync))
            {
                throw new AmazonS3Exception("source precondition failed")
                {
                    StatusCode = HttpStatusCode.PreconditionFailed
                };
            }
            return Default(method.ReturnType);
        });
        using var storage = new R2FileStorage(client, "private-bucket");

        var result = await storage.PromoteAsync(
            "quarantine/key",
            "ready/key",
            "source-etag",
            metadata);

        Assert.Equal(PromotionResult.SourceChanged, result);
        Assert.Equal(2, sourceHeads);
    }

    [Fact]
    public async Task StoreValidated_DisablesStreamingSigV4FeaturesUnsupportedByR2()
    {
        var metadata = ReadyMetadata(Guid.NewGuid());
        PutObjectRequest? put = null;
        var readyExists = false;
        var client = CreateClient((method, arguments) =>
        {
            if (method.Name == nameof(IAmazonS3.GetObjectMetadataAsync))
            {
                var request = Assert.IsType<GetObjectMetadataRequest>(arguments[0]);
                if (request.Key == "ready/key" && !readyExists)
                {
                    return NotFound<GetObjectMetadataResponse>();
                }
                return Task.FromResult(
                    request.Key == "ready/key"
                        ? ObjectResponse("ready-etag", metadata)
                        : ObjectResponse("source-etag", null));
            }
            if (method.Name == nameof(IAmazonS3.PutObjectAsync))
            {
                put = Assert.IsType<PutObjectRequest>(arguments[0]);
                readyExists = true;
                return Task.FromResult(new PutObjectResponse());
            }
            return Default(method.ReturnType);
        });
        using var storage = new R2FileStorage(client, "private-bucket");
        await using var content = new MemoryStream(new byte[metadata.Size]);

        var result = await storage.StoreValidatedAsync(
            "quarantine/key",
            "ready/key",
            "source-etag",
            metadata,
            content);

        Assert.Equal(ValidatedWriteResult.Written, result);
        Assert.NotNull(put);
        Assert.True(put.DisablePayloadSigning);
        Assert.True(put.DisableDefaultChecksumValidation);
        Assert.Equal("*", put.IfNoneMatch);
        Assert.Same(content, put.InputStream);
        Assert.False(put.AutoCloseStream);
    }

    [Fact]
    public async Task DeleteIfExists_MissingR2Object_IsSuccessful()
    {
        var calls = 0;
        var client = CreateClient((method, _) =>
        {
            if (method.Name == nameof(IAmazonS3.DeleteObjectAsync))
            {
                calls++;
                return NotFound<DeleteObjectResponse>();
            }
            return Default(method.ReturnType);
        });
        using var storage = new R2FileStorage(client, "private-bucket");

        await storage.DeleteIfExistsAsync("quarantine/key");

        Assert.Equal(1, calls);
    }

    private static ReadyObjectMetadata ReadyMetadata(Guid attachmentId) =>
        new(
            attachmentId,
            "source-etag",
            new string('a', 64),
            "application/pdf",
            1234);

    private static GetObjectMetadataResponse ObjectResponse(
        string etag,
        ReadyObjectMetadata? metadata)
    {
        var response = new GetObjectMetadataResponse
        {
            ETag = etag,
            ContentLength = metadata?.Size ?? 1234
        };
        response.Headers.ContentType = metadata?.MediaType ?? "application/pdf";
        if (metadata is not null)
        {
            response.Metadata["attachment-id"] = metadata.AttachmentId.ToString("D");
            response.Metadata["source-etag"] = metadata.SourceETag;
            response.Metadata["sha256"] = metadata.Sha256;
            response.Metadata["media-type"] = metadata.MediaType;
            response.Metadata["size"] = metadata.Size.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }
        return response;
    }

    private static IAmazonS3 CreateClient(
        Func<MethodInfo, object?[], object?> handler)
    {
        var client = DispatchProxy.Create<IAmazonS3, S3DispatchProxy>();
        ((S3DispatchProxy)(object)client).Handler = handler;
        return client;
    }

    private static Task<T> NotFound<T>() =>
        Task.FromException<T>(new AmazonS3Exception("not found")
        {
            StatusCode = HttpStatusCode.NotFound
        });

    private static object? Default(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }
        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }
        if (returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var valueType = returnType.GetGenericArguments()[0];
            var value = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
            return typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(valueType)
                .Invoke(null, [value]);
        }
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }

    private class S3DispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            Handler(
                targetMethod ?? throw new InvalidOperationException(),
                args ?? []);
    }
}
