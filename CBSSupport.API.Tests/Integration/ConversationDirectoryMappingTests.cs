using System.Data;
using CBSSupport.Shared.Contracts;
using Dapper;

namespace CBSSupport.API.Tests.Integration;

public sealed class ConversationDirectoryMappingTests
{
    [Fact]
    public void DirectoryUser_BigintProjection_DapperMaterializesRecord()
    {
        using var table = new DataTable();
        table.Columns.Add("Id", typeof(long));
        table.Columns.Add("DisplayName", typeof(string));
        table.Rows.Add(109L, "Test Client User");

        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());

        var user = reader.GetRowParser<ConversationDirectoryUser>()(reader);

        Assert.Equal(109L, user.Id);
        Assert.Equal("Test Client User", user.DisplayName);
    }

    [Fact]
    public void ClientDirectoryQuery_CastsIntegerPrincipalIdToBigint()
    {
        var repositorySource = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "CBSSupport.Shared",
            "Services",
            "ConversationRepository.cs")));

        Assert.Matches(
            @"SELECT\s+CAST\(id AS bigint\) AS Id,\s+COALESCE\(full_name, user_name\) AS DisplayName\s+FROM internal\.support_users",
            repositorySource);
    }
}
