using System.Text.Json;
using CBSSupport.Shared.Contracts;

namespace CBSSupport.API.Tests.Attachments;

public sealed class AttachmentMetadataLeakTests
{
    [Fact]
    public void MessageAndStatusMetadata_DoNotExposeStorageKeysOrSignedUrls()
    {
        var summary = new AttachmentSummary(
            Guid.NewGuid(),
            "document.pdf",
            "application/pdf",
            1234,
            AttachmentStates.Ready,
            null,
            1);
        var message = new ConversationMessage(
            10,
            20,
            null,
            DateTime.UtcNow,
            new ConversationSender(30, "Client", "Client"),
            Guid.NewGuid(),
            4,
            [summary]);
        var status = new AttachmentStatusResponse(
            summary.Id,
            20,
            summary.DisplayName,
            summary.MediaType,
            summary.Size,
            summary.Status,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(24));

        var json = JsonSerializer.Serialize(new { message, status });

        Assert.DoesNotContain("quarantine", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("readyKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signedUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uploadUrl", json, StringComparison.OrdinalIgnoreCase);
    }
}
