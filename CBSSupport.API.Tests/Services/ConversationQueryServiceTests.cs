using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Services;

public sealed class ConversationQueryServiceTests
{
    [Theory]
    [MemberData(nameof(CancelledOperations))]
    public async Task QueryOperation_CancelledBeforeDatabaseAccess_ThrowsOperationCanceledException(
        Func<ConversationQueryService, CancellationToken, Task> operation)
    {
        var service = new ConversationQueryService(
            "Host=localhost;Database=unused",
            NullLogger<ConversationQueryService>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(service, cancellation.Token));
    }

    [Fact]
    public void ConversationQueryService_SeparatesLegacyReadsFromDurableCommands()
    {
        var methods = typeof(IConversationQueryService).GetMethods();

        Assert.Contains(methods, method => method.Name == nameof(IConversationQueryService.GetMessagesAsync));
        Assert.DoesNotContain(methods, method => method.Name.Contains("Send", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, method => method.Name.Contains("Create", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> CancelledOperations()
    {
        yield return [
            (Func<ConversationQueryService, CancellationToken, Task>)(async (service, token) =>
                await service.GetSidebarAsync(42, token))];
        yield return [
            (Func<ConversationQueryService, CancellationToken, Task>)(async (service, token) =>
                await service.GetInstructionTicketsForUserAsync(42, 7, token))];
        yield return [
            (Func<ConversationQueryService, CancellationToken, Task>)(async (service, token) =>
                await service.GetConversationsByInstructionTypeAsync(100, cancellationToken: token))];
        yield return [
            (Func<ConversationQueryService, CancellationToken, Task>)(async (service, token) =>
                await service.GetInstructionByIdAsync(10, cancellationToken: token))];
        yield return [
            (Func<ConversationQueryService, CancellationToken, Task>)(async (service, token) =>
                await service.GetMessagesAsync(10, cancellationToken: token))];
    }
}
