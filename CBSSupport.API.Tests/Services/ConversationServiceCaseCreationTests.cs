using System.Text.Json;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Tests.Services;

public sealed class ConversationServiceCaseCreationTests
{
    [Fact]
    public async Task CreateCase_Ticket_NormalizesTextAndPreservesLegacyRemarksShape()
    {
        var repository = new RecordingRepository();
        var service = new ConversationService(repository);
        var actor = new ConversationActor(7, 42, IsAdmin: false, "Client User");

        var result = await service.CreateCaseAsync(
            actor,
            ConversationTypes.TrainingTicket,
            InstructionCategories.Ticket,
            "  Unable to post.  ",
            "High",
            "Occurs after approval.",
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            "127.0.0.1");

        Assert.Equal(ConversationCommandStatus.Created, result.Status);
        Assert.Equal("Unable to post.", repository.Text);
        Assert.Equal(actor, repository.Actor);
        Assert.Equal(ConversationTypes.TrainingTicket, repository.InstructionTypeId);
        Assert.Equal(InstructionCategories.Ticket, repository.InstructionCategoryId);
        Assert.Equal(DateTimeKind.Utc, repository.OccurredAt.Kind);

        using var remarks = JsonDocument.Parse(repository.PersistedRemarks!);
        Assert.Equal("High", remarks.RootElement.GetProperty("priority").GetString());
        Assert.Equal(
            "Occurs after approval.",
            remarks.RootElement.GetProperty("userremarks").GetString());
        Assert.Equal("Training", remarks.RootElement.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task CreateCase_Inquiry_PreservesPlainRemarks()
    {
        var repository = new RecordingRepository();
        var service = new ConversationService(repository);

        var result = await service.CreateCaseAsync(
            new ConversationActor(7, 42, IsAdmin: false, "Client User"),
            ConversationTypes.AccountsInquiry,
            InstructionCategories.Inquiry,
            "Account question",
            priority: null,
            remarks: "Please clarify.",
            expiryDate: null,
            ipAddress: null);

        Assert.Equal(ConversationCommandStatus.Created, result.Status);
        Assert.Equal("Please clarify.", repository.PersistedRemarks);
    }

    [Fact]
    public async Task CreateCase_AdminOrMismatchedShape_IsRejectedBeforeRepository()
    {
        var repository = new RecordingRepository();
        var service = new ConversationService(repository);

        var adminResult = await service.CreateCaseAsync(
            new ConversationActor(9, null, IsAdmin: true, "Admin"),
            ConversationTypes.TrainingTicket,
            InstructionCategories.Ticket,
            "Ticket",
            null,
            null,
            null,
            null);
        var mismatchResult = await service.CreateCaseAsync(
            new ConversationActor(7, 42, IsAdmin: false, "Client"),
            ConversationTypes.TrainingTicket,
            InstructionCategories.Inquiry,
            "Ticket",
            null,
            null,
            null,
            null);

        Assert.Equal(ConversationCommandStatus.Invalid, adminResult.Status);
        Assert.Equal(ConversationCommandStatus.Invalid, mismatchResult.Status);
        Assert.Equal(0, repository.CreateCalls);
    }

    private sealed class RecordingRepository : IConversationRepository
    {
        public int CreateCalls { get; private set; }
        public ConversationActor? Actor { get; private set; }
        public short? InstructionTypeId { get; private set; }
        public short? InstructionCategoryId { get; private set; }
        public string? Text { get; private set; }
        public string? PersistedRemarks { get; private set; }
        public DateTime OccurredAt { get; private set; }

        public Task<ConversationCommandResult<ChatMessage>> CreateCaseAsync(
            ConversationActor actor,
            short instructionTypeId,
            short instructionCategoryId,
            string text,
            string? persistedRemarks,
            DateTime? expiryDate,
            string? ipAddress,
            DateTime occurredAt,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            Actor = actor;
            InstructionTypeId = instructionTypeId;
            InstructionCategoryId = instructionCategoryId;
            Text = text;
            PersistedRemarks = persistedRemarks;
            OccurredAt = occurredAt;
            return Task.FromResult(new ConversationCommandResult<ChatMessage>(
                ConversationCommandStatus.Created,
                new ChatMessage
                {
                    Id = 101,
                    InstructionId = 101,
                    ConversationSequence = 1,
                    Instruction = text,
                    InstTypeId = instructionTypeId,
                    InstCategoryId = instructionCategoryId,
                    ClientId = actor.ClientId
                }));
        }

        public Task<ConversationAccess?> GetForAdminAsync(
            long conversationId,
            long adminUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationAccess?>(null);

        public Task<ConversationAccess?> GetForClientAsync(
            long conversationId,
            long clientId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationAccess?>(null);

        public Task<long?> InsertMessageForAdminAsync(
            long conversationId,
            int userId,
            string text,
            DateTime sentAt,
            string? ipAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(null);

        public Task<long?> InsertMessageForClientAsync(
            long conversationId,
            long clientId,
            int userId,
            string text,
            DateTime sentAt,
            string? ipAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(null);
    }
}
