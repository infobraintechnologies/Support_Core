using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Tests.Integration;

public sealed class LegacyCaseConversationAccessIntegrationTests
{
    [PostgreSqlIntegrationFact]
    public async Task GetAccess_TicketAndInquiryRoots_UsesTrustedTenantAndCanonicalRoot()
    {
        await using var fixture = await SignalRPostgreSqlFixture.CreateAsync();
        var repository = new ConversationRepository(
            fixture.ApplicationConnectionString,
            attachmentsEnabled: false);

        var ticket = await repository.GetForClientAsync(
            SignalRPostgreSqlFixture.TicketConversationId,
            SignalRPostgreSqlFixture.RevokedClient.ClientId,
            SignalRPostgreSqlFixture.RevokedClient.UserId);
        var inquiry = await repository.GetForClientAsync(
            SignalRPostgreSqlFixture.InquiryConversationId,
            SignalRPostgreSqlFixture.RevokedClient.ClientId,
            SignalRPostgreSqlFixture.RevokedClient.UserId);
        var wrongTenant = await repository.GetForClientAsync(
            SignalRPostgreSqlFixture.TicketConversationId,
            SignalRPostgreSqlFixture.RevokedClient.ClientId + 1,
            SignalRPostgreSqlFixture.RevokedClient.UserId);
        var reply = await repository.GetForClientAsync(
            SignalRPostgreSqlFixture.TicketReplyId,
            SignalRPostgreSqlFixture.RevokedClient.ClientId,
            SignalRPostgreSqlFixture.RevokedClient.UserId);
        var admin = await repository.GetForAdminAsync(
            SignalRPostgreSqlFixture.TicketConversationId,
            adminUserId: 77);

        Assert.NotNull(ticket);
        Assert.Equal(ConversationTypes.TrainingTicket, ticket.InstructionTypeId);
        Assert.NotNull(inquiry);
        Assert.Equal(ConversationTypes.AccountsInquiry, inquiry.InstructionTypeId);
        Assert.Null(wrongTenant);
        Assert.Null(reply);
        Assert.NotNull(admin);
    }
}
