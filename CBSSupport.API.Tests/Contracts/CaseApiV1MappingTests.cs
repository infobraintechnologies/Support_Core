using System.Text.Json;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.ViewModels;

namespace CBSSupport.API.Tests.Contracts;

public sealed class CaseApiV1MappingTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TicketCreateMapping_PopulatesPublicFieldsAndMapsTypeLabel()
    {
        var created = new ChatMessage
        {
            Id = 234,
            InstructionId = 234,
            DateTime = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc),
            InstTypeId = ConversationTypes.MigrationTicket,
            ClientId = 1001,
            SenderName = "Example User",
            Instruction = "Observed after closing batch 18.",
            Completed = false
        };

        var response = CaseDtoMapper.ToTicket(created, "Posting mismatch", CasePriorities.High, "Observed after closing batch 18.");

        Assert.Equal(234, response.Id);
        Assert.Equal(234, response.ConversationId);
        Assert.Equal("Posting mismatch", response.Subject);
        Assert.Equal(CaseTypes.Migration, response.Type);
        Assert.Equal(CasePriorities.High, response.Priority);
        Assert.Equal("Open", response.Status);
        Assert.Equal(1001, response.ClientId);
        Assert.Equal("Example User", response.CreatedByName);
    }

    [Fact]
    public void TicketCreateMapping_NullPriority_DefaultsToNormal()
    {
        var created = new ChatMessage { Id = 1, InstTypeId = ConversationTypes.SetupTicket };

        var response = CaseDtoMapper.ToTicket(created, "Subject", null, "Description");

        Assert.Equal(CasePriorities.Normal, response.Priority);
    }

    [Fact]
    public void TicketResponseSerialization_DoesNotExposePersistenceInternals()
    {
        var created = new ChatMessage
        {
            Id = 234,
            InstructionId = 234,
            DateTime = DateTime.UtcNow,
            InstTypeId = ConversationTypes.MigrationTicket,
            ClientId = 1001,
            SenderName = "Example User",
            InsertUser = 1,
            ClientAuthUserId = 7,
            InstChannel = "chat"
        };

        var json = JsonSerializer.Serialize(
            CaseDtoMapper.ToTicket(created, "Subj", CasePriorities.High, "Desc"),
            WebOptions);

        Assert.Contains("\"createdByName\"", json);
        Assert.DoesNotContain("senderName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientAuthUserId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insertUser", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instTypeId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instChannel", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TicketReadMapping_MapsViewModelToResponse()
    {
        var view = new TicketViewModel
        {
            Id = 12,
            Subject = "Migration",
            Date = DateTime.UtcNow,
            CreatedBy = "Alice",
            ResolvedBy = "Bob",
            Status = "Resolved",
            Priority = "High",
            ClientId = 55,
            Description = "body",
            Remarks = "remarks",
            InstTypeId = ConversationTypes.MigrationTicket,
            ExpiryDate = null,
            ResolvedDate = DateTime.UtcNow
        };

        var response = CaseDtoMapper.ToTicket(view);

        Assert.Equal(12, response.Id);
        Assert.Equal(CaseTypes.Migration, response.Type);
        Assert.Equal("Resolved", response.Status);
        Assert.Equal("Alice", response.CreatedByName);
        Assert.Equal("Bob", response.ResolvedByName);
        Assert.Equal(55, response.ClientId);
    }

    [Fact]
    public void InquiryReadMapping_MapsViewModelTopicToResponse()
    {
        var view = new InquiryViewModel
        {
            Id = 7,
            Topic = "Accounts",
            InquiredBy = "Alice",
            Date = DateTime.UtcNow,
            Outcome = "Pending",
            ClientId = 9,
            Description = "body",
            Priority = "Normal",
            InstTypeId = ConversationTypes.AccountsInquiry
        };

        var response = CaseDtoMapper.ToInquiry(view);

        Assert.Equal(7, response.Id);
        Assert.Equal(CaseTypes.Accounts, response.Type);
        Assert.Equal("Pending", response.Status);
        Assert.Equal("Alice", response.InquiredByName);
    }

    [Fact]
    public void InquiryResponseSerialization_DoesNotExposePersistenceInternals()
    {
        var view = new InquiryViewModel
        {
            Id = 7,
            Date = DateTime.UtcNow,
            InstTypeId = ConversationTypes.AccountsInquiry,
            ClientId = 1001,
            InquiredBy = "Example User",
            Description = "Clarification requested."
        };

        var json = JsonSerializer.Serialize(CaseDtoMapper.ToInquiry(view), WebOptions);

        Assert.Contains("\"inquiredByName\"", json);
        Assert.DoesNotContain("instTypeId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientAuthUserId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insertUser", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("senderName", json, StringComparison.OrdinalIgnoreCase);
    }
}
