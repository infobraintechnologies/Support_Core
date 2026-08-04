using System.Data;
using System.Reflection;
using CBSSupport.Shared.Services;
using Dapper;

namespace CBSSupport.API.Tests.Integration;

public sealed class AttachmentProjectionMappingTests
{
    [Fact]
    public void ConversationAttachmentRow_PostgreSqlSmallIntPosition_DapperMaterializes()
    {
        using var table = CreateAttachmentTable(includeMessageIdFirst: true);
        table.Rows.Add(
            123L,
            Guid.NewGuid(),
            "document.pdf",
            "application/pdf",
            1024L,
            "Ready",
            DBNull.Value,
            (short)1);

        var row = MaterializeNestedRow(
            table,
            typeof(ConversationRepository),
            "AttachmentMessageRow");

        Assert.Equal(
            (short)1,
            row.GetType().GetProperty("Position")!.GetValue(row));
    }

    [Fact]
    public void ConversationAttachmentBindingRow_NullableSmallIntPosition_DapperMaterializes()
    {
        using var table = CreateAttachmentTable(includeMessageIdFirst: false);
        table.Columns.Add("MessageId", typeof(long));
        table.Rows.Add(
            Guid.NewGuid(),
            "document.pdf",
            "application/pdf",
            1024L,
            "Ready",
            DBNull.Value,
            DBNull.Value,
            DBNull.Value);

        var row = MaterializeNestedRow(
            table,
            typeof(ConversationRepository),
            "AttachmentBindingRow");

        Assert.Null(row.GetType().GetProperty("Position")!.GetValue(row));
    }

    [Fact]
    public void OutboxAttachmentRow_PostgreSqlSmallIntPosition_DapperMaterializes()
    {
        using var table = CreateAttachmentTable(includeMessageIdFirst: true);
        table.Rows.Add(
            123L,
            Guid.NewGuid(),
            "document.pdf",
            "application/pdf",
            1024L,
            "Ready",
            DBNull.Value,
            (short)1);

        var row = MaterializeNestedRow(
            table,
            typeof(ConversationOutboxRepository),
            "OutboxAttachmentRow");

        Assert.Equal(
            (short)1,
            row.GetType().GetProperty("Position")!.GetValue(row));
    }

    private static object MaterializeNestedRow(
        DataTable table,
        Type repositoryType,
        string rowTypeName)
    {
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var rowType = repositoryType.GetNestedType(
            rowTypeName,
            BindingFlags.NonPublic);
        Assert.NotNull(rowType);

        return reader.GetRowParser(rowType)(reader);
    }

    private static DataTable CreateAttachmentTable(bool includeMessageIdFirst)
    {
        var table = new DataTable();
        if (includeMessageIdFirst)
        {
            table.Columns.Add("MessageId", typeof(long));
        }
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("DisplayName", typeof(string));
        table.Columns.Add("MediaType", typeof(string));
        table.Columns.Add("Size", typeof(long));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("RejectionCode", typeof(string));
        table.Columns.Add("Position", typeof(short));
        return table;
    }
}
