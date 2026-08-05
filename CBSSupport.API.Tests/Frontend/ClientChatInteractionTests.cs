namespace CBSSupport.API.Tests.Frontend;

public sealed class ClientChatInteractionTests
{
    [Fact]
    public void ClientSend_UsesConversationIdentityInsteadOfLegacyRouteGuard()
    {
        var source = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));

        Assert.DoesNotContain("!currentChatContext.route", source, StringComparison.Ordinal);
        Assert.Contains("Number.isSafeInteger(conversationId)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientSendButton_TracksEveryInputChange()
    {
        var source = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));

        Assert.Contains(
            "function updateSendButtonState()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "messageInput.addEventListener(\"input\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClientSend_UsesHttpMessagingClientInsteadOfHubCommand()
    {
        var source = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));

        Assert.Contains("await messaging.send(", source, StringComparison.Ordinal);
        Assert.Contains("attachmentIds);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("invoke(\"SendMessage\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TicketAndInquiryChats_UseMessagingV2HistoryAndSend()
    {
        var chatSource = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));

        Assert.Contains("await messaging.join(currentChatContext.id)", chatSource, StringComparison.Ordinal);
        Assert.Contains("await messaging.reconcile(conversationId)", chatSource, StringComparison.Ordinal);
        Assert.Contains("await messaging.send(", chatSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/api/instructions/messages/", chatSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/v1/api/instructions/reply\"", chatSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientConversationList_UsesClaimScopedMessagingV2Contract()
    {
        var source = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));

        Assert.Contains("messaging.listConversations({ limit: 100 })", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/api/instructions/sidebar/", source, StringComparison.Ordinal);
        Assert.Contains("applyConversationFilters", source, StringComparison.Ordinal);
        Assert.Contains("conversation.kind === \"Private\"", source, StringComparison.Ordinal);
        Assert.Contains("conversation.adminDisplayName", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientPrivatePicker_UsesServerAuthorizedAdminDirectory()
    {
        var script = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));
        var view = File.ReadAllText(GetApiPath("Views/Support/Index.cshtml"));

        Assert.Contains("await messaging.getAvailableAdmins()", script, StringComparison.Ordinal);
        Assert.Contains("await messaging.getOrCreatePrivate(adminId)", script, StringComparison.Ordinal);
        Assert.Contains("id=\"newPrivateChatModal\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"available-admins-search\"", view, StringComparison.Ordinal);
        Assert.Contains("filterAvailableAdmins", script, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Available administrators\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientMessages_UseSafeDomAndExposeRetryState()
    {
        var source = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));

        Assert.Contains("appendTextElement(bubble, \"p\", \"message-text\"", source, StringComparison.Ordinal);
        Assert.Contains("messaging.on(\"sendstate\"", source, StringComparison.Ordinal);
        Assert.Contains("retry-message", source, StringComparison.Ordinal);
        Assert.DoesNotContain("row.innerHTML", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientReadCursor_AdvancesOnlyForVisibleActiveConversation()
    {
        var source = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));

        Assert.Contains("document.visibilityState === \"hidden\"", source, StringComparison.Ordinal);
        Assert.Contains("await messaging.advanceRead(conversationId, throughSequence)", source, StringComparison.Ordinal);
        Assert.Contains("messaging.cancelReconcile?.(currentChatContext.id)", source, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener(\"visibilitychange\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientComposer_IsMultilineAndHasMobileBackNavigation()
    {
        var script = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));
        var view = File.ReadAllText(GetApiPath("Views/Support/Index.cshtml"));

        Assert.Contains("<textarea class=\"form-control\"", view, StringComparison.Ordinal);
        Assert.Contains("Shift+Enter for a new line", view, StringComparison.Ordinal);
        Assert.Contains("e.key === \"Enter\" && !e.shiftKey", script, StringComparison.Ordinal);
        Assert.Contains("id=\"mobile-conversation-back\"", view, StringComparison.Ordinal);
    }

    private static string GetApiPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "CBSSupport.API",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
