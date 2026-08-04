using CBSSupport.Shared.Services;
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
        Assert.Contains("{InstructionId}", source);
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
