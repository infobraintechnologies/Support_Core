namespace CBSSupport.API.Tests.Frontend;

public sealed class AdminMessagingV2BrowserTests
{
    [Fact]
    public void AdminChat_LoadsV2SummariesIndependentlyFromLegacyTenantSections()
    {
        var source = ReadApiFile("wwwroot/js/admin/admin-chat.js");

        Assert.Contains("/api/v1/conversations?limit=100", source, StringComparison.Ordinal);
        Assert.Contains("listV2Conversations()", source, StringComparison.Ordinal);
        Assert.Contains("loadLegacySidebar(currentClientId)", source, StringComparison.Ordinal);
        Assert.Contains("No assigned private chats.", source, StringComparison.Ordinal);
        Assert.Contains("applyConversationFilters", source, StringComparison.Ordinal);
        Assert.Contains("String(summary.kind).toLowerCase() === \"private\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/api/instructions/sidebar/${currentClientId}`);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminTicketAndInquiryChats_UseMessagingV2WhileInternalRetainsLegacyAdapter()
    {
        var chatSource = ReadApiFile("wwwroot/js/admin/admin-chat.js");
        var signalRSource = ReadApiFile("wwwroot/js/admin/admin-signalR.js");

        Assert.Contains(
            "type === \"ticket\" || type === \"inquiry\"",
            chatSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "? \"messaging-v2\"",
            chatSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "await messaging()?.reconcile(Number(conversationId))",
            chatSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"/v1/api/instructions/reply\"",
            signalRSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!useMessagingV2)",
            signalRSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AdminPrivateChat_UsesTenantDirectoryAndExactCounterparty()
    {
        var source = ReadApiFile("wwwroot/js/admin/admin-chat.js");

        Assert.Contains(
            "/conversation-users",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "counterpartyUserId: clientUserId",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "getOrCreatePrivate(clientUserId)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AdminConversationLifecycle_UsesVersionAndRefreshesConflicts()
    {
        var source = ReadApiFile("wwwroot/js/admin/admin-chat.js");

        Assert.Contains("expectedVersion: mainChatContext.version", source, StringComparison.Ordinal);
        Assert.Contains("/assignment", source, StringComparison.Ordinal);
        Assert.Contains("/archive", source, StringComparison.Ordinal);
        Assert.Contains("loadTransferAdmins", source, StringComparison.Ordinal);
        Assert.Contains("getAvailableAdmins()", source, StringComparison.Ordinal);
        Assert.Contains("error.status === 409", source, StringComparison.Ordinal);
        Assert.Contains("leaveConversation(oldId)", source, StringComparison.Ordinal);
        Assert.Contains("cancelReconcile", source, StringComparison.Ordinal);
        Assert.Contains("handleConversationChanged", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminChat_RendersUntrustedMessageFieldsWithTextContent()
    {
        var source = ReadApiFile("wwwroot/js/admin/admin-chat.js");

        Assert.Contains("text.textContent = message.instruction || \"\";", source, StringComparison.Ordinal);
        Assert.Contains("title.textContent = data.name || \"Conversation\";", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "${AdminUtils.escapeHtml(message.instruction",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AdminAttachments_ExcludeInternalAndSupportFloatingCaseChats()
    {
        var chatSource = ReadApiFile("wwwroot/js/admin/admin-chat.js");
        var signalRSource = ReadApiFile("wwwroot/js/admin/admin-signalR.js");

        Assert.Contains("function supportsAttachments(context)", chatSource, StringComparison.Ordinal);
        Assert.Contains("context?.isV2", chatSource, StringComparison.Ordinal);
        Assert.Contains("[\"group\", \"private\", \"ticket\", \"inquiry\"]", chatSource, StringComparison.Ordinal);
        Assert.Contains("floatingAttachmentComposers", chatSource, StringComparison.Ordinal);
        Assert.Contains("floating-attachment-upload-list", chatSource, StringComparison.Ordinal);
        Assert.Contains("composer?.clearBound(attachmentIds)", chatSource, StringComparison.Ordinal);
        Assert.Contains("message.attachments", chatSource, StringComparison.Ordinal);
        Assert.Contains("message.attachments", signalRSource, StringComparison.Ordinal);
        Assert.Contains("renderMessageAttachments", signalRSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminView_UsesCaseSensitiveScriptsAndAccessibleMobileControls()
    {
        var source = ReadApiFile("Views/AdminSupport/Index.cshtml");

        Assert.Contains("~/js/admin/admin-notification.js", source, StringComparison.Ordinal);
        Assert.Contains("~/js/admin/admin-signalR.js", source, StringComparison.Ordinal);
        Assert.DoesNotContain("~/js/admin/admin-notifications.js", source, StringComparison.Ordinal);
        Assert.DoesNotContain("~/js/admin/admin-signalr.js", source, StringComparison.Ordinal);
        Assert.Contains("id=\"admin-chat-back-btn\"", source, StringComparison.Ordinal);
        Assert.Contains("id=\"new-private-chat-form\"", source, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", source, StringComparison.Ordinal);
        Assert.Contains("<textarea class=\"form-control\"", source, StringComparison.Ordinal);
        Assert.Contains("Shift+Enter for a new line", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminCore_LoadsIdentityBeforeCreatingUserScopedMessagingClient()
    {
        var source = ReadApiFile("wwwroot/js/admin/admin-core.js");

        var identityIndex = source.IndexOf("fetch('/v1/api/accounts/me')", StringComparison.Ordinal);
        var signalRIndex = source.IndexOf("window.AdminSignalR.initialize()", StringComparison.Ordinal);

        Assert.True(identityIndex >= 0);
        Assert.True(signalRIndex > identityIndex);
    }

    [Fact]
    public void AdminChat_ShowsUnreadAndAdvancesActiveReadCursor()
    {
        var source = ReadApiFile("wwwroot/js/admin/admin-chat.js");

        Assert.Contains("summary.unreadCount", source, StringComparison.Ordinal);
        Assert.Contains("renderUnreadBadge", source, StringComparison.Ordinal);
        Assert.Contains("throughSequence: mainChatContext.latestSequence", source, StringComparison.Ordinal);
        Assert.Contains("markActiveConversationRead()", source, StringComparison.Ordinal);
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
