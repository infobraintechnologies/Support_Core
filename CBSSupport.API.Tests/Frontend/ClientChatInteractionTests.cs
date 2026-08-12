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
    public void ClientPrivatePicker_IsNotRenderedOrRequestedWhilePrivateMessagingIsDisabled()
    {
        var script = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));
        var view = File.ReadAllText(GetApiPath("Views/Support/Index.cshtml"));

        Assert.Contains("var privateMessagingEnabled = MessagingFeatures.Value.PrivateEnabled;", view, StringComparison.Ordinal);
        Assert.Contains("@if (privateMessagingEnabled)", view, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await loadConversations();\n        loadAvailableAdmins();",
            script,
            StringComparison.Ordinal);
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

    [Fact]
    public void ClientDateSeparator_WrapsLabelSoDividerDoesNotCrossText()
    {
        var source = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));

        Assert.Contains(
            "label.textContent = formatDateForSeparator(msgDateStr);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ds.appendChild(label);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientUi_UsesBootstrapIconsInsteadOfFontAwesome()
    {
        var view = File.ReadAllText(GetApiPath("Views/Support/Index.cshtml"));
        var layout = File.ReadAllText(GetApiPath("Views/Shared/_Layout.cshtml"));
        var source = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));

        Assert.Contains("bootstrap-icons@1.11.3", view, StringComparison.Ordinal);
        Assert.Contains("bootstrap-icons@1.11.3", layout, StringComparison.Ordinal);
        Assert.Contains("bi bi-send", view, StringComparison.Ordinal);
        Assert.Contains("bi bi-ticket-perforated", source, StringComparison.Ordinal);
        Assert.DoesNotContain("font-awesome", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fas fa-", view, StringComparison.Ordinal);
        Assert.DoesNotContain("fas fa-", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientHeaderActionIcons_AreVisibleOnTheLightHeader()
    {
        var view = File.ReadAllText(GetApiPath("Views/Support/Index.cshtml"));
        var styles = File.ReadAllText(GetApiPath("wwwroot/css/site.css"));

        Assert.Contains("id=\"fullscreen-btn\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"client-notification-btn\"", view, StringComparison.Ordinal);
        Assert.Contains(".client-portal .app-header .btn-icon,", styles, StringComparison.Ordinal);
        Assert.Contains("color: var(--color-text-secondary);", styles, StringComparison.Ordinal);
        Assert.Contains(".client-portal .app-header .btn-icon:focus-visible,", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientConversationSidebar_UsesClearTypesAndSelectableConversationStates()
    {
        var view = File.ReadAllText(GetApiPath("Views/Support/Index.cshtml"));
        var source = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));
        var styles = File.ReadAllText(GetApiPath("wwwroot/css/site.css"));

        Assert.Contains("Support chats, tickets, and inquiries", view, StringComparison.Ordinal);
        Assert.Contains("id=\"client-conversation-count\"", view, StringComparison.Ordinal);
        Assert.Contains("class=\"client-conversation-search\"", view, StringComparison.Ordinal);
        Assert.Contains("function getConversationIconClass(kind)", source, StringComparison.Ordinal);
        Assert.Contains("bi-ticket-perforated", source, StringComparison.Ordinal);
        Assert.Contains("client-conversation-icon", source, StringComparison.Ordinal);
        Assert.Contains(".client-portal #client-conversation-list .conversation-item.active", styles, StringComparison.Ordinal);
        Assert.Contains(".client-portal .client-conversation-icon", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr);", styles, StringComparison.Ordinal);
        Assert.Contains(".client-portal .client-conversation-heading #new-private-chat-btn", styles, StringComparison.Ordinal);
        Assert.Contains("width: 100%;", styles, StringComparison.Ordinal);
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
