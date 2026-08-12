using CBSSupport.API.Attachments;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace CBSSupport.API.Tests.Attachments;

public sealed class LocalAttachmentStorageTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "cbs-support-attachments",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteAsync_StoresOpaqueFileInFlatWebRootUploadsDirectory()
    {
        var storage = CreateStorage();
        const string key = "53684801-bcd9-4841-96ad-7bae78c22084.pending.pdf";
        var bytes = "%PDF-test"u8.ToArray();

        var stored = await storage.WriteAsync(
            key,
            new MemoryStream(bytes),
            AttachmentContentValidator.PdfMediaType,
            bytes.Length);

        var uploadRoot = Path.Combine(_testRoot, "wwwroot", "Uploads");
        Assert.Equal(key, stored.Key);
        Assert.True(File.Exists(Path.Combine(uploadRoot, key)));
        Assert.Single(Directory.GetFiles(uploadRoot));
        Assert.Empty(Directory.GetDirectories(uploadRoot));
        Assert.DoesNotContain("invoice-july", stored.Key, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("tenant-42/file.pdf")]
    [InlineData("conversation-25\\file.pdf")]
    [InlineData("C:\\outside.pdf")]
    [InlineData("invoice-july.pdf")]
    public async Task StorageOperations_PathLikeKey_IsRejected(string key)
    {
        var storage = CreateStorage();

        await Assert.ThrowsAsync<InvalidDataException>(() => storage.WriteAsync(
            key,
            new MemoryStream("test"u8.ToArray()),
            AttachmentContentValidator.PdfMediaType,
            4));
    }

    [Fact]
    public async Task StoreValidatedAsync_WritesGuidExtensionReadyFileAndLeavesNoDirectories()
    {
        var storage = CreateStorage();
        var attachmentId = Guid.Parse("53684801-bcd9-4841-96ad-7bae78c22084");
        var sourceKey = $"{attachmentId:D}.pending.pdf";
        var readyKey = $"{attachmentId:D}.pdf";
        var bytes = "%PDF-test"u8.ToArray();
        var source = await storage.WriteAsync(
            sourceKey,
            new MemoryStream(bytes),
            AttachmentContentValidator.PdfMediaType,
            bytes.Length);
        var metadata = new ReadyObjectMetadata(
            attachmentId,
            source.ETag,
            source.ETag,
            AttachmentContentValidator.PdfMediaType,
            bytes.Length);

        var result = await storage.StoreValidatedAsync(
            sourceKey,
            readyKey,
            source.ETag,
            metadata,
            new MemoryStream(bytes));
        await storage.DeleteIfExistsAsync(sourceKey);

        var uploadRoot = Path.Combine(_testRoot, "wwwroot", "Uploads");
        Assert.Equal(ValidatedWriteResult.Written, result);
        Assert.True(File.Exists(Path.Combine(uploadRoot, readyKey)));
        Assert.False(File.Exists(Path.Combine(uploadRoot, sourceKey)));
        Assert.Empty(Directory.GetDirectories(uploadRoot));
    }

    [Fact]
    public async Task WriteAsync_ExistingDifferentBytes_FailsClosedWithoutOverwrite()
    {
        var storage = CreateStorage();
        const string key = "53684801-bcd9-4841-96ad-7bae78c22084.pending.pdf";
        await storage.WriteAsync(
            key,
            new MemoryStream("first"u8.ToArray()),
            AttachmentContentValidator.PdfMediaType,
            5);

        await Assert.ThrowsAsync<AttachmentStorageConflictException>(() => storage.WriteAsync(
            key,
            new MemoryStream("other"u8.ToArray()),
            AttachmentContentValidator.PdfMediaType,
            5));

        var path = Path.Combine(_testRoot, "wwwroot", "Uploads", key);
        Assert.Equal("first", await File.ReadAllTextAsync(path));
    }

    private LocalAttachmentStorage CreateStorage() =>
        new(
            new TestWebHostEnvironment
            {
                ContentRootPath = _testRoot,
                WebRootPath = Path.Combine(_testRoot, "wwwroot")
            },
            new AttachmentOptions());

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "CBSSupport.API.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
