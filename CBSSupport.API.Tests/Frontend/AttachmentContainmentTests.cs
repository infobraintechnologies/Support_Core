using System.Text.Json;

namespace CBSSupport.API.Tests.Frontend;

public sealed class AttachmentContainmentTests
{
    [Fact]
    public void Application_DoesNotExposeLegacyUploadController()
    {
        var controller = typeof(Program).Assembly.GetType(
            "CBSSupport.API.Controllers.FileUploadController");

        Assert.Null(controller);
    }

    [Theory]
    [InlineData("Views/Support/Index.cshtml")]
    [InlineData("Views/AdminSupport/Index.cshtml")]
    public void ChatView_RendersFeatureGatedAttachmentControls(string relativePath)
    {
        var source = File.ReadAllText(GetApiPath(relativePath));

        Assert.Contains("data-attachments-enabled=", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AttachmentCapability.CanCreateUploadIntents", source, StringComparison.Ordinal);
        Assert.Contains("AttachmentUiCapability AttachmentCapability", source, StringComparison.Ordinal);
        Assert.Contains("type=\"file\"", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attachment-button", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "~/js/messaging/attachments.js\" asp-append-version=\"true\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("hidden disabled", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientChat_UsesSharedAttachmentComposerWithoutLegacyUploadEndpoint()
    {
        var source = File.ReadAllText(GetApiPath("wwwroot/js/chat.js"));

        Assert.Contains("CBSSupportAttachments?.createComposer", source, StringComparison.Ordinal);
        Assert.Contains("attachmentComposer?.getReadyIds()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UploadFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/FileUpload", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Application_DisabledAttachmentsGateConversationRepositorySql()
    {
        var program = File.ReadAllText(GetApiPath("Program.cs"));
        var repository = File.ReadAllText(GetSharedPath(
            "Services/ConversationRepository.cs"));
        var outboxRepository = File.ReadAllText(GetSharedPath(
            "Services/ConversationOutboxRepository.cs"));

        Assert.Contains(
            "new ConversationRepository(connectionString, attachmentOptions.Enabled)",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ConversationOutboxRepository(connectionString, attachmentOptions.Enabled)",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "_attachmentsEnabled",
            repository,
            StringComparison.Ordinal);
        Assert.Contains(
            "ErrorCode: \"attachments_disabled\"",
            repository,
            StringComparison.Ordinal);
        Assert.Contains(
            "messageIds.Length == 0 || !attachmentsEnabled",
            outboxRepository,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentLaunchProfiles_EnableStructuralAttachmentCapability()
    {
        using var launchSettings = JsonDocument.Parse(File.ReadAllText(
            GetApiPath("Properties/launchSettings.json")));
        var profiles = launchSettings.RootElement.GetProperty("profiles");

        foreach (var profile in profiles.EnumerateObject())
        {
            var environment = profile.Value.GetProperty("environmentVariables");

            Assert.Equal(
                "true",
                environment.GetProperty("Attachments__Enabled").GetString());
            Assert.Equal(
                "StructuralValidationOnly",
                environment.GetProperty("Attachments__SecurityMode").GetString());
            Assert.Equal(
                "false",
                environment.GetProperty("Attachments__Scanning__WorkerEnabled").GetString());
        }
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

    private static string GetSharedPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "CBSSupport.Shared",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
