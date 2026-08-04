namespace CBSSupport.API.Tests.Frontend;

public sealed class MessagingV2BrowserTests
{
    [Fact]
    public void MessagingApi_UsesClientMessageIdAndSequenceCursor()
    {
        var source = ReadMessagingScript("api.js");

        Assert.Contains("clientMessageId: request.clientMessageId", source, StringComparison.Ordinal);
        Assert.Contains("afterSequence", source, StringComparison.Ordinal);
        Assert.Contains("/messages", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMessage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MessagingApi_ExposesConversationDirectoryReadAndLifecycleCommands()
    {
        var source = ReadMessagingScript("api.js");

        Assert.Contains("listConversations", source, StringComparison.Ordinal);
        Assert.Contains("getAvailableAdmins", source, StringComparison.Ordinal);
        Assert.Contains("getAvailableClientUsers", source, StringComparison.Ordinal);
        Assert.Contains("counterpartyUserId", source, StringComparison.Ordinal);
        Assert.Contains("advanceRead", source, StringComparison.Ordinal);
        Assert.Contains("/assignment", source, StringComparison.Ordinal);
        Assert.Contains("/archive", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MessagingTransport_RejoinsEveryAuthorizedConversation()
    {
        var source = ReadMessagingScript("transport.js");

        Assert.Contains("connection.onreconnected", source, StringComparison.Ordinal);
        Assert.Contains("for (const conversationId of joinedConversations)", source, StringComparison.Ordinal);
        Assert.Contains("JoinConversation", source, StringComparison.Ordinal);
        Assert.Contains("nextRetryDelayInMilliseconds", source, StringComparison.Ordinal);
        Assert.Contains("scheduleStartRetry", source, StringComparison.Ordinal);
        Assert.Contains("ConversationChanged", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MessagingStore_DeduplicatesAndPreservesDrafts()
    {
        var source = ReadMessagingScript("store.js");

        Assert.Contains("state.messageIds.has", source, StringComparison.Ordinal);
        Assert.Contains("state.clientMessageIds.has", source, StringComparison.Ordinal);
        Assert.Contains("saveDraft", source, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem", source, StringComparison.Ordinal);
        Assert.Contains("savePending", source, StringComparison.Ordinal);
        Assert.Contains("loadPending", source, StringComparison.Ordinal);
        Assert.Contains("readPendingCollection", source, StringComparison.Ordinal);
        Assert.Contains("listPending: getPending", source, StringComparison.Ordinal);
        Assert.Contains("upsertConversation", source, StringComparison.Ordinal);
        Assert.Contains("markConversationRead", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MessagingClient_ReusesUncertainSendIdentifierAndReportsState()
    {
        var source = ReadMessagingScript("client.js");

        Assert.Contains("existingPending?.text === normalizedText", source, StringComparison.Ordinal);
        Assert.Contains("state: \"failed\"", source, StringComparison.Ordinal);
        Assert.Contains("state: \"pending\"", source, StringComparison.Ordinal);
        Assert.Contains("state: \"sent\"", source, StringComparison.Ordinal);
        Assert.Contains("sequence-gap", source, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", source, StringComparison.Ordinal);
        Assert.Contains("periodic-reconcile", source, StringComparison.Ordinal);
        Assert.Contains("retry", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminView_UsesPinnedLocalSignalRClient()
    {
        var source = File.ReadAllText(GetApiPath("Views/AdminSupport/Index.cshtml"));

        Assert.Contains("~/js/signalr/dist/browser/signalr.min.js", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cdnjs.cloudflare.com/ajax/libs/microsoft-signalr", source, StringComparison.Ordinal);
    }

    private static string ReadMessagingScript(string fileName) =>
        File.ReadAllText(GetApiPath($"wwwroot/js/messaging/{fileName}"));

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
