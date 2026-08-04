using System.Text.Json;
using CBSSupport.Shared.Contracts;

namespace CBSSupport.API.Tests.Contracts;

public sealed class ConversationMessageSerializationTests
{
    [Fact]
    public void Message_ExposesOnlyDocumentedAttachmentSummaries()
    {
        var attachment = new AttachmentSummary(
            Guid.NewGuid(),
            "evidence.pdf",
            "application/pdf",
            1234,
            AttachmentStates.Ready,
            null,
            0);
        var message = new ConversationMessage(
            501,
            25,
            null,
            DateTime.UtcNow,
            new ConversationSender(7, "Client User", "Client"),
            Guid.NewGuid(),
            9,
            [attachment]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            message,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.True(json.RootElement.TryGetProperty("attachments", out var attachments));
        Assert.Equal(1, attachments.GetArrayLength());
        Assert.False(json.RootElement.TryGetProperty("safeAttachments", out _));
        var serializedAttachment = attachments[0];
        Assert.False(serializedAttachment.TryGetProperty("readyKey", out _));
        Assert.False(serializedAttachment.TryGetProperty("quarantineKey", out _));
        Assert.False(serializedAttachment.TryGetProperty("uploadUrl", out _));
    }
}
