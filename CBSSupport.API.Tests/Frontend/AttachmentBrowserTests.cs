namespace CBSSupport.API.Tests.Frontend;

public sealed class AttachmentBrowserTests
{
    [Fact]
    public void SharedComposer_UsesAuthorizedSameOriginPutAndRequiredPollingSchedule()
    {
        var source = ReadApiFile("wwwroot/js/messaging/attachments.js");

        Assert.Contains("new XMLHttpRequest()", source, StringComparison.Ordinal);
        Assert.Contains("xhr.open(\"PUT\", intent.uploadUrl, true)", source, StringComparison.Ordinal);
        Assert.Contains("intent.requiredHeaders", source, StringComparison.Ordinal);
        Assert.Contains("RequestVerificationToken", source, StringComparison.Ordinal);
        Assert.Contains("getResponseHeader(\"ETag\")", source, StringComparison.Ordinal);
        Assert.Contains("elapsed < 30000 ? 2000 : 5000", source, StringComparison.Ordinal);
        Assert.Contains("5 * 60 * 1000", source, StringComparison.Ordinal);
        Assert.Contains("\"Check status\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedComposer_AdvertisesOnlyStructurallySupportedFileTypes()
    {
        var source = ReadApiFile("wwwroot/js/messaging/attachments.js");

        Assert.Contains(
            "const accepted = \".pdf,.jpg,.jpeg,.png,.docx,.xlsx\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("application/vnd.openxmlformats-officedocument.wordprocessingml.document", source);
        Assert.Contains("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", source);
        Assert.DoesNotContain("image/webp", source, StringComparison.Ordinal);
        Assert.DoesNotContain("text/plain", source, StringComparison.Ordinal);
        Assert.DoesNotContain("text/csv", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedComposer_CancelsAbandonedAndReplacedIntents()
    {
        var source = ReadApiFile("wwwroot/js/messaging/attachments.js");

        Assert.Contains("await cancelRemote(intent.id, false)", source, StringComparison.Ordinal);
        Assert.Contains("if (previousId) await cancelRemote(previousId, true)", source, StringComparison.Ordinal);
        Assert.Contains("method: \"DELETE\"", source, StringComparison.Ordinal);
        Assert.Contains("keepalive: true", source, StringComparison.Ordinal);
        Assert.Contains("global.addEventListener(\"pagehide\"", source, StringComparison.Ordinal);
        Assert.Contains("items.get(item.localId) !== item", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedComposer_UsesSafeNavigationAndDoesNotPersistFilesOrUrls()
    {
        var source = ReadApiFile("wwwroot/js/messaging/attachments.js");

        Assert.Contains("document.createElement(\"img\")", source, StringComparison.Ordinal);
        Assert.Contains("document.createElement(\"a\")", source, StringComparison.Ordinal);
        Assert.Contains("/content", source, StringComparison.Ordinal);
        Assert.Contains("textContent", source, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", source, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileReader", source, StringComparison.Ordinal);
        Assert.DoesNotContain("canvas", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttachmentStyles_CoverUploadsMessagesAndFloatingComposer()
    {
        var source = ReadApiFile("wwwroot/css/chat.css");

        Assert.Contains(".attachment-upload-item", source, StringComparison.Ordinal);
        Assert.Contains(".message-attachments img", source, StringComparison.Ordinal);
        Assert.Contains(".floating-chat-controls", source, StringComparison.Ordinal);
        Assert.Contains(".floating-attachment-upload-list", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientAndAdminComposers_RestrictAttachmentsToApprovedConversationKinds()
    {
        var client = ReadApiFile("wwwroot/js/chat.js");
        var admin = ReadApiFile("wwwroot/js/admin/admin-chat.js");

        Assert.Contains("function supportsAttachments(context)", client, StringComparison.Ordinal);
        Assert.Contains("[\"group\", \"private\", \"ticket\", \"inquiry\"]", client, StringComparison.Ordinal);
        Assert.Contains("function supportsAttachments(context)", admin, StringComparison.Ordinal);
        Assert.Contains("context?.isV2", admin, StringComparison.Ordinal);
        Assert.Contains("[\"group\", \"private\", \"ticket\", \"inquiry\"]", admin, StringComparison.Ordinal);
        Assert.DoesNotContain("\"internal\"]", client, StringComparison.Ordinal);
        Assert.DoesNotContain("\"internal\"]", admin, StringComparison.Ordinal);
        Assert.Contains("void attachmentComposer?.resetForConversation()", client, StringComparison.Ordinal);
        Assert.Contains("navigation.dataset.page !== \"chats\"", admin, StringComparison.Ordinal);
    }

    private static string ReadApiFile(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "CBSSupport.API",
            relativePath.Replace('/', Path.DirectorySeparatorChar))));
}
