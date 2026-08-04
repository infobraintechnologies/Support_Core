using System.Data;
using System.Reflection;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Dapper;

namespace CBSSupport.API.Tests.Integration;

public sealed class ConversationOutboxRepositoryMappingTests
{
    [Fact]
    public void OutboxRow_PostgreSqlProjectionTypes_DapperMaterializesRow()
    {
        using var table = CreateProjectionTable();
        table.Rows.Add(
            Guid.NewGuid(),
            123L,
            DBNull.Value,
            "ConversationArchived",
            (short)1,
            DateTime.UtcNow,
            1,
            42L,
            "Private",
            "Active",
            101L,
            7,
            1L,
            "Active",
            1L,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value);

        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var rowType = typeof(ConversationOutboxRepository).GetNestedType(
            "OutboxRow",
            BindingFlags.NonPublic);

        Assert.NotNull(rowType);
        var parser = reader.GetRowParser(rowType);
        var row = parser(reader);

        Assert.NotNull(row);
        Assert.Equal(
            (short)1,
            rowType.GetProperty("SchemaVersion")!.GetValue(row));
        Assert.Equal(
            7,
            rowType.GetProperty("AdminUserId")!.GetValue(row));
    }

    [Fact]
    public void ToItem_AttachmentOnlyMessage_PreservesMetadata()
    {
        var rowType = typeof(ConversationOutboxRepository).GetNestedType(
            "OutboxRow",
            BindingFlags.NonPublic);
        var toItem = typeof(ConversationOutboxRepository).GetMethod(
            "ToItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(rowType);
        Assert.NotNull(toItem);

        var eventId = Guid.NewGuid();
        var clientMessageId = Guid.NewGuid();
        var sentAt = DateTime.UtcNow;
        var row = Activator.CreateInstance(
            rowType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                eventId, 123L, 456L, "MessageCreated", (short)1, sentAt, 0,
                42L, "Group", "Active", null, null, 1L, "Active", 1L,
                456L, null, sentAt, 99L, "Client", "Client", clientMessageId, 7L
            ],
            culture: null);
        var attachment = new AttachmentSummary(
            Guid.NewGuid(),
            "document.pdf",
            "application/pdf",
            1234,
            AttachmentStates.Ready,
            null,
            1);

        var item = Assert.IsType<ConversationOutboxItem>(
            toItem.Invoke(null, [row, new[] { attachment }]));

        Assert.NotNull(item.Message);
        Assert.Null(item.Message.Text);
        Assert.Equal(attachment, Assert.Single(item.Message.SafeAttachments));
    }

    private static DataTable CreateProjectionTable()
    {
        var table = new DataTable();
        table.Columns.Add("EventId", typeof(Guid));
        table.Columns.Add("ConversationId", typeof(long));
        table.Columns.Add("MessageId", typeof(long));
        table.Columns.Add("EventType", typeof(string));
        table.Columns.Add("SchemaVersion", typeof(short));
        table.Columns.Add("OccurredAt", typeof(DateTime));
        table.Columns.Add("AttemptCount", typeof(int));
        table.Columns.Add("ClientId", typeof(long));
        table.Columns.Add("ConversationKind", typeof(string));
        table.Columns.Add("ConversationState", typeof(string));
        table.Columns.Add("ClientUserId", typeof(int));
        table.Columns.Add("AdminUserId", typeof(int));
        table.Columns.Add("AccessVersion", typeof(long));
        table.Columns.Add("CurrentState", typeof(string));
        table.Columns.Add("CurrentVersion", typeof(long));
        table.Columns.Add("MessageRecordId", typeof(long));
        table.Columns.Add("MessageText", typeof(string));
        table.Columns.Add("MessageSentAt", typeof(DateTime));
        table.Columns.Add("SenderUserId", typeof(long));
        table.Columns.Add("SenderKind", typeof(string));
        table.Columns.Add("SenderDisplayName", typeof(string));
        table.Columns.Add("ClientMessageId", typeof(Guid));
        table.Columns.Add("Sequence", typeof(long));
        return table;
    }
}
