using CBSSupport.Shared.Services;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Services;

public sealed class ChatServiceLoggingTests
{
    private const string SourceRelativePath = @"CBSSupport.Shared\Services\ChatService.cs";

    [Fact]
    public void Constructor_AcceptsInjectedLogger()
    {
        var service = new ChatService(
            "Host=localhost;Database=unused",
            NullLogger<ChatService>.Instance);

        Assert.NotNull(service);
    }

    [Theory]
    [InlineData("Console.Write")]
    [InlineData("Console.Error")]
    [InlineData("Debug.WriteLine")]
    [InlineData("JsonSerializer.Serialize")]
    [InlineData("StackTrace")]
    public void ChatService_DoesNotEmitSensitiveDiagnostics(string forbidden)
    {
        var source = ReadChatServiceSource();

        Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatService_UsesStructuredLoggerTemplates()
    {
        var source = ReadChatServiceSource();

        Assert.Contains("_logger.LogWarning", source);
        Assert.Contains("{InstructionTypeId}", source);
    }

    [Theory]
    [InlineData(ConversationTypes.TrainingTicket, InstructionCategories.Ticket)]
    [InlineData(ConversationTypes.AccountsInquiry, InstructionCategories.Inquiry)]
    public async Task CreateInstructionTicketAsync_CaseCommand_IsRejectedBeforeAnyLegacyDatabaseWrite(
        short instructionTypeId,
        short instructionCategoryId)
    {
        var service = new ChatService(
            "Host=localhost;Database=unused",
            NullLogger<ChatService>.Instance);

        var result = await service.CreateInstructionTicketAsync(new ChatMessage
        {
            InstTypeId = instructionTypeId,
            InstCategoryId = instructionCategoryId,
            Instruction = "This must use IConversationService.",
            InsertUser = 9
        });

        Assert.Null(result);
    }

    private static string ReadChatServiceSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "CBSSupportSolution.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, SourceRelativePath);
        Assert.True(File.Exists(path), $"Expected source file at {path}");
        return File.ReadAllText(path);
    }
}
