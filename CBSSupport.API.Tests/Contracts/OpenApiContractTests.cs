using System.Text;
using System.Text.Json;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Readers;

namespace CBSSupport.API.Tests.Contracts;

public sealed class OpenApiContractTests
{
    private static string ArtifactPath => Path.Combine(
        FindRepositoryRoot(),
        "artifacts",
        "openapi",
        "cbs-support-api.json");

    [Fact]
    public void BuildGeneratedDocument_IsValidAndDescribesOnlyVersionedPublicApi()
    {
        var json = ReadArtifact();
        var reader = new OpenApiStringReader();
        var document = reader.Read(json, out var diagnostic);

        Assert.NotNull(document);
        Assert.Empty(diagnostic.Errors);
        using (var jsonDocument = JsonDocument.Parse(json))
        {
            Assert.Equal("3.0.1", jsonDocument.RootElement.GetProperty("openapi").GetString());
        }
        Assert.NotEmpty(document.Paths);
        Assert.All(document.Paths.Keys, path =>
            Assert.StartsWith("/api/v1/", path, StringComparison.Ordinal));
        Assert.DoesNotContain("v1/api", json, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("cookieAuth", document.Components.SecuritySchemes.Keys);
        Assert.Contains("bearerAuth", document.Components.SecuritySchemes.Keys);
        Assert.Contains("csrfToken", document.Components.SecuritySchemes.Keys);
        Assert.Contains("ProblemDetails", document.Components.Schemas.Keys);
        Assert.Contains("PageCursor", document.Components.Schemas.Keys);
        Assert.Contains("PageSize", document.Components.Schemas.Keys);
        Assert.Contains("ConcurrencyToken", document.Components.Schemas.Keys);

        Assert.DoesNotContain(document.Components.Schemas.Keys, name =>
            name.Contains("ViewModel", StringComparison.Ordinal)
            || name.Contains("ChatMessage", StringComparison.Ordinal)
            || name.Contains("Instruction", StringComparison.OrdinalIgnoreCase));

        foreach (var path in document.Paths.Values)
        {
            foreach (var operation in path.Operations.Values)
            {
                Assert.False(string.IsNullOrWhiteSpace(operation.OperationId));
                Assert.Contains("400", operation.Responses.Keys);
                Assert.Contains("401", operation.Responses.Keys);
                Assert.Contains("403", operation.Responses.Keys);
                Assert.Contains("500", operation.Responses.Keys);
                Assert.Contains("application/problem+json", operation.Responses["400"].Content.Keys);
            }
        }
    }

    [Fact]
    public void BuildGeneratedDocument_DescribesPagingFilteringSortingAndConcurrency()
    {
        var reader = new OpenApiStringReader();
        var document = reader.Read(ReadArtifact(), out var diagnostic);
        Assert.Empty(diagnostic.Errors);

        var caseList = document.Paths["/api/v1/tickets"].Operations[Microsoft.OpenApi.Models.OperationType.Get];
        var queryNames = caseList.Parameters
            .Where(parameter => parameter.In == Microsoft.OpenApi.Models.ParameterLocation.Query)
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("PageSize", queryNames);
        Assert.Contains("Cursor", queryNames);
        Assert.Contains("Sort", queryNames);
        Assert.Contains("Direction", queryNames);
        Assert.Contains("Status", queryNames);
        Assert.Contains("Priority", queryNames);

        var pageSize = caseList.Parameters.Single(parameter =>
            string.Equals(parameter.Name, "PageSize", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("PageSize", pageSize.Schema.Reference.Id);
        var sort = caseList.Parameters.Single(parameter =>
            string.Equals(parameter.Name, "Sort", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sort.Schema.Enum, value =>
            value is OpenApiString stringValue && stringValue.Value == "createdAt");

        Assert.Contains(document.Components.Schemas.Values, schema =>
            schema.Properties.Values.Any(property => property.Reference?.Id == "ConcurrencyToken"));
        Assert.Contains("409", document.Paths["/api/v1/admin/tickets/{caseId}/status"]
            .Operations[Microsoft.OpenApi.Models.OperationType.Put].Responses.Keys);

        var readCursor = document.Paths["/api/v1/conversations/{conversationId}/read"]
            .Operations[Microsoft.OpenApi.Models.OperationType.Put];
        Assert.DoesNotContain("200", readCursor.Responses.Keys);
        Assert.Contains("204", readCursor.Responses.Keys);

        var content = document.Paths["/api/v1/attachments/{attachmentId}/content"]
            .Operations[Microsoft.OpenApi.Models.OperationType.Get];
        Assert.DoesNotContain("200", content.Responses.Keys);
        Assert.Contains("302", content.Responses.Keys);
    }

    private static string ReadArtifact()
    {
        Assert.True(File.Exists(ArtifactPath),
            $"The build-time OpenAPI artifact was not found at '{ArtifactPath}'. Build CBSSupport.API before running contract tests.");
        return File.ReadAllText(ArtifactPath, Encoding.UTF8);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CBSSupportSolution.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
