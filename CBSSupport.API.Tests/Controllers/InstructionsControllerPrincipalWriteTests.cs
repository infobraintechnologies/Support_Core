using System.Reflection;
using System.Security.Claims;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Tests.Controllers;

public sealed class InstructionsControllerPrincipalWriteTests
{
    [Fact]
    public async Task SaveTicketTraining_ClientPrincipal_DelegatesToConversationBoundary()
    {
        var (controller, chatService, conversations) =
            CreateController(CreateClientPrincipal(userId: 7, clientId: 42));

        var result = await controller.SaveTicketTraining(new CreateInstructionRequest(
            "Unable to post a transaction.",
            null,
            null,
            null,
            null));

        Assert.IsType<ObjectResult>(result);
        Assert.Empty(chatService.Calls);
        Assert.NotNull(conversations.Actor);
        Assert.Equal(7, conversations.Actor.UserId);
        Assert.Equal(42, conversations.Actor.ClientId);
        Assert.False(conversations.Actor.IsAdmin);
        Assert.Equal(ConversationTypes.TrainingTicket, conversations.InstructionTypeId);
        Assert.Equal(InstructionCategories.Ticket, conversations.InstructionCategoryId);
        Assert.Equal("Unable to post a transaction.", conversations.Text);
    }

    [Fact]
    public async Task SaveInternalTeamChat_AdminPrincipal_UsesOnlyAdminAuthorColumns()
    {
        var (controller, service, conversations) =
            CreateController(CreateAdminPrincipal(userId: 9));
        service.ReturnValues[nameof(IChatService.CreateInstructionTicketAsync)] =
            Task.FromResult<ChatMessage?>(null);

        var result = await controller.SaveInternalTeamChat(new CreateInstructionRequest(
            "Review the incident.",
            null,
            null,
            null,
            null));

        Assert.IsType<ObjectResult>(result);
        var instruction = GetSavedInstruction(service);
        Assert.Equal(9, instruction.InsertUser);
        Assert.Equal(9, instruction.UserId);
        Assert.Null(instruction.ClientUserId);
        Assert.Null(instruction.ClientAuthUserId);
        Assert.Null(instruction.ClientId);
        Assert.Null(conversations.Actor);
    }

    private static ChatMessage GetSavedInstruction(RecordingChatServiceProxy service)
    {
        var call = Assert.Single(service.Calls);
        Assert.Equal(nameof(IChatService.CreateInstructionTicketAsync), call.MethodName);
        return Assert.IsType<ChatMessage>(call.Arguments[0]);
    }

    private static (
        InstructionsController Controller,
        RecordingChatServiceProxy ChatService,
        RecordingConversationService Conversations) CreateController(
        ClaimsPrincipal principal)
    {
        var chatService = DispatchProxy.Create<IChatService, RecordingChatServiceProxy>();
        var recordingService = (RecordingChatServiceProxy)(object)chatService;
        var conversations = new RecordingConversationService();
        var controller = new InstructionsController(chatService, null!, conversations, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

        return (controller, recordingService, conversations);
    }

    private sealed class RecordingConversationService : IConversationService
    {
        public ConversationActor? Actor { get; private set; }
        public short? InstructionTypeId { get; private set; }
        public short? InstructionCategoryId { get; private set; }
        public string? Text { get; private set; }

        public Task<ConversationAccess?> GetAccessAsync(
            long conversationId,
            ConversationActor actor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationAccess?>(null);

        public Task<ConversationMessage?> CreateMessageAsync(
            long conversationId,
            ConversationActor actor,
            string text,
            string? ipAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationMessage?>(null);

        public Task<ConversationCommandResult<ChatMessage>> CreateCaseAsync(
            ConversationActor actor,
            short instructionTypeId,
            short instructionCategoryId,
            string text,
            string? priority,
            string? remarks,
            DateTime? expiryDate,
            string? ipAddress,
            CancellationToken cancellationToken = default,
            string? subject = null)
        {
            Actor = actor;
            InstructionTypeId = instructionTypeId;
            InstructionCategoryId = instructionCategoryId;
            Text = text;
            return Task.FromResult(new ConversationCommandResult<ChatMessage>(
                ConversationCommandStatus.Invalid,
                ErrorCode: "test_stop"));
        }
    }

    private static ClaimsPrincipal CreateClientPrincipal(long userId, long clientId) =>
        CreatePrincipal(
            userId,
            Roles.Client,
            [new Claim(CustomClaimTypes.ClientId, clientId.ToString())]);

    private static ClaimsPrincipal CreateAdminPrincipal(long userId) =>
        CreatePrincipal(userId, Roles.Admin, []);

    private static ClaimsPrincipal CreatePrincipal(
        long userId,
        string role,
        IReadOnlyCollection<Claim> additionalClaims)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role),
                .. additionalClaims
            ],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}
