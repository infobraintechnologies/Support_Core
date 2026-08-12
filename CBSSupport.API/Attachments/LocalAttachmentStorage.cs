using System.Security.Cryptography;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Attachments;

public sealed class LocalAttachmentStorage : IFileStorage
{
    private static readonly HashSet<string> AllowedSuffixes = new(
        [
            ".pdf", ".jpg", ".png", ".docx", ".xlsx",
            ".pending.pdf", ".pending.jpg", ".pending.jpeg", ".pending.png",
            ".pending.docx", ".pending.xlsx"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly string _uploadRoot;

    public LocalAttachmentStorage(
        IWebHostEnvironment environment,
        AttachmentOptions options)
    {
        var webRoot = environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");
        }

        _uploadRoot = Path.GetFullPath(Path.Combine(webRoot, options.UploadPath));
    }

    public async Task<StoredObjectInfo> WriteAsync(
        string key,
        Stream content,
        string mediaType,
        long size,
        CancellationToken cancellationToken = default)
    {
        if (size < 1)
        {
            throw new InvalidDataException("Attachment content must not be empty.");
        }

        var targetPath = ResolvePath(key);
        if (!string.Equals(
            MediaTypeFromPath(targetPath),
            mediaType,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Attachment media type does not match its generated storage name.");
        }
        Directory.CreateDirectory(_uploadRoot);
        var temporaryPath = Path.Combine(_uploadRoot, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyExactAsync(content, destination, size, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            try
            {
                File.Move(temporaryPath, targetPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                var existing = await GetInfoAsync(key, targetPath, cancellationToken);
                var pending = await GetInfoAsync(key, temporaryPath, cancellationToken);
                if (existing.Size != pending.Size
                    || !string.Equals(existing.ETag, pending.ETag, StringComparison.Ordinal))
                {
                    throw new AttachmentStorageConflictException(
                        "A different attachment already exists for the generated storage name.");
                }
            }

            return await GetInfoAsync(key, targetPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<StoredObjectInfo?> HeadAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        return File.Exists(path)
            ? await GetInfoAsync(key, path, cancellationToken)
            : null;
    }

    public async Task<StoredObjectRead?> OpenReadAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var info = await GetInfoAsync(key, path, cancellationToken);
        var content = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new StoredObjectRead(info, content);
    }

    public async Task<PromotionResult> PromoteAsync(
        string quarantineKey,
        string readyKey,
        string expectedSourceETag,
        ReadyObjectMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        await using var source = await OpenReadAsync(quarantineKey, cancellationToken);
        if (source is null)
        {
            return PromotionResult.MissingSource;
        }
        if (!ETagEquals(source.Info.ETag, expectedSourceETag))
        {
            return PromotionResult.SourceChanged;
        }

        var existing = await HeadAsync(readyKey, cancellationToken);
        if (existing is not null)
        {
            return ExistingMatches(existing, metadata)
                ? PromotionResult.ExistingExactMatch
                : PromotionResult.ReadyConflict;
        }

        try
        {
            var written = await WriteAsync(
                readyKey,
                source.Content,
                metadata.MediaType,
                metadata.Size,
                cancellationToken);
            return ExistingMatches(written, metadata)
                ? PromotionResult.Copied
                : PromotionResult.ReadyConflict;
        }
        catch (AttachmentStorageConflictException)
        {
            return PromotionResult.RetryableConflict;
        }
    }

    public async Task<ValidatedWriteResult> StoreValidatedAsync(
        string quarantineKey,
        string readyKey,
        string expectedSourceETag,
        ReadyObjectMetadata metadata,
        Stream validatedContent,
        CancellationToken cancellationToken = default)
    {
        var source = await HeadAsync(quarantineKey, cancellationToken);
        if (source is null || !ETagEquals(source.ETag, expectedSourceETag))
        {
            return ValidatedWriteResult.SourceChanged;
        }

        var existing = await HeadAsync(readyKey, cancellationToken);
        if (existing is not null)
        {
            return ExistingMatches(existing, metadata)
                ? ValidatedWriteResult.ExistingExactMatch
                : ValidatedWriteResult.ReadyConflict;
        }

        try
        {
            var written = await WriteAsync(
                readyKey,
                validatedContent,
                metadata.MediaType,
                metadata.Size,
                cancellationToken);
            return ExistingMatches(written, metadata)
                ? ValidatedWriteResult.Written
                : ValidatedWriteResult.ReadyConflict;
        }
        catch (AttachmentStorageConflictException)
        {
            return ValidatedWriteResult.RetryableConflict;
        }
    }

    public Task DeleteIfExistsAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string ResolvePath(string key)
    {
        if (string.IsNullOrWhiteSpace(key)
            || !string.Equals(Path.GetFileName(key), key, StringComparison.Ordinal)
            || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || key.Length <= 36
            || !Guid.TryParseExact(key[..36], "D", out _)
            || !AllowedSuffixes.Contains(key[36..]))
        {
            throw new InvalidDataException("Attachment storage key is invalid.");
        }

        var path = Path.GetFullPath(Path.Combine(_uploadRoot, key));
        var rootPrefix = _uploadRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _uploadRoot
            : _uploadRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Attachment storage path escaped the upload root.");
        }
        return path;
    }

    private static async Task<StoredObjectInfo> GetInfoAsync(
        string key,
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return new StoredObjectInfo(
            key,
            stream.Length,
            Convert.ToHexString(digest).ToLowerInvariant(),
            MediaTypeFromPath(path),
            new Dictionary<string, string>());
    }

    private static async Task CopyExactAsync(
        Stream source,
        Stream destination,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > expectedSize)
            {
                throw new InvalidDataException("Attachment exceeded its declared size.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total != expectedSize)
        {
            throw new InvalidDataException("Attachment did not match its declared size.");
        }
    }

    private static bool ExistingMatches(StoredObjectInfo value, ReadyObjectMetadata metadata) =>
        value.Size == metadata.Size
        && string.Equals(value.ETag, metadata.Sha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(value.ContentType, metadata.MediaType, StringComparison.OrdinalIgnoreCase);

    private static bool ETagEquals(string left, string right) =>
        string.Equals(
            left.Trim().Trim('"'),
            right.Trim().Trim('"'),
            StringComparison.OrdinalIgnoreCase);

    private static string MediaTypeFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => AttachmentContentValidator.PdfMediaType,
            ".jpg" or ".jpeg" => AttachmentContentValidator.JpegMediaType,
            ".png" => AttachmentContentValidator.PngMediaType,
            ".docx" => AttachmentContentValidator.DocxMediaType,
            ".xlsx" => AttachmentContentValidator.XlsxMediaType,
            _ => "application/octet-stream"
        };
}
