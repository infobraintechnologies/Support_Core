using System.Text.Json;
using CBSSupport.API.Hubs;
using CBSSupport.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace CBSSupport.API.Tests.Integration;

public sealed class SignalRRevocationIntegrationTests
{
    [PostgreSqlIntegrationFact]
    public async Task RevokedIdleClient_IsDisconnectedAndCannotReceiveConversationBroadcast()
    {
        await using var fixture = await SignalRPostgreSqlFixture.CreateAsync();
        await using var revokedConnection = fixture.CreateHubConnection(
            SignalRPostgreSqlFixture.RevokedClient);
        await using var observerConnection = fixture.CreateHubConnection(
            SignalRPostgreSqlFixture.ObserverClient);
        var revokedClosed = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var revokedPreRevocationMessage = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var revokedPostRevocationMessage = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observerPostRevocationMessage = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        revokedConnection.Closed += exception =>
        {
            revokedClosed.TrySetResult(exception);
            return Task.CompletedTask;
        };
        revokedConnection.On<JsonElement>("MessageCreated", message =>
        {
            var messageId = message.GetProperty("id").GetInt64();
            if (messageId == 7000)
            {
                revokedPreRevocationMessage.TrySetResult(message);
            }
            else if (messageId == 7001)
            {
                revokedPostRevocationMessage.TrySetResult(message);
            }
        });
        observerConnection.On<JsonElement>("MessageCreated", message =>
        {
            if (message.GetProperty("id").GetInt64() == 7001)
            {
                observerPostRevocationMessage.TrySetResult(message);
            }
        });

        await revokedConnection.StartAsync();
        await observerConnection.StartAsync();
        await revokedConnection.InvokeAsync(
            "JoinConversation",
            SignalRPostgreSqlFixture.ConversationId);
        await observerConnection.InvokeAsync(
            "JoinConversation",
            SignalRPostgreSqlFixture.ConversationId);

        var hubContext = fixture.Factory.Services.GetRequiredService<IHubContext<ChatHub>>();
        await hubContext.Clients
            .Group(RealtimeGroupNames.Conversation(SignalRPostgreSqlFixture.ConversationId))
            .SendAsync("MessageCreated", CreateMessage(7000, "pre-revocation message"));
        var receivedBeforeRevocation = await revokedPreRevocationMessage.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7000, receivedBeforeRevocation.GetProperty("id").GetInt64());

        await fixture.RevokeClientAsync(SignalRPostgreSqlFixture.RevokedClient.UserId);

        await revokedClosed.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Equal(HubConnectionState.Disconnected, revokedConnection.State);

        var publishedMessage = CreateMessage(7001, "post-revocation message");
        await hubContext.Clients
            .Group(RealtimeGroupNames.Conversation(SignalRPostgreSqlFixture.ConversationId))
            .SendAsync("MessageCreated", publishedMessage);

        var observed = await observerPostRevocationMessage.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(publishedMessage.Id, observed.GetProperty("id").GetInt64());
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(revokedPostRevocationMessage.Task.IsCompleted);
    }

    private static ConversationMessage CreateMessage(long id, string text) =>
        new(
            id,
            SignalRPostgreSqlFixture.ConversationId,
            text,
            DateTime.UtcNow,
            new ConversationSender(
                SignalRPostgreSqlFixture.ObserverClient.UserId,
                SignalRPostgreSqlFixture.ObserverClient.DisplayName,
                "client"),
            Attachments: []);
}
