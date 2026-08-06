using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace CBSSupport.API.OpenApi;

/// <summary>Normalizes operation-level errors and public query contracts.</summary>
public sealed class ApiOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var path = context.Description.RelativePath ?? string.Empty;
        var method = context.Description.HttpMethod?.ToUpperInvariant() ?? "GET";

        if (string.IsNullOrWhiteSpace(operation.OperationId))
        {
            operation.OperationId = CreateOperationId(method, path);
        }
        if (method is "POST" or "PUT" or "PATCH" or "DELETE")
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [Reference("cookieAuth")] = [],
                    [Reference("csrfToken")] = []
                },
                new OpenApiSecurityRequirement
                {
                    [Reference("bearerAuth")] = []
                }
            ];
        }

        operation.Tags ??= [];
        if (operation.Tags.Count == 0)
        {
            operation.Tags.Add(new OpenApiTag { Name = GetTag(path) });
        }

        AddPagingAndFilteringMetadata(operation);
        AddConcurrencyMetadata(operation);
        AddExpectedResponses(operation, method, path);
        return Task.CompletedTask;
    }

    private static void AddPagingAndFilteringMetadata(OpenApiOperation operation)
    {
        foreach (var parameter in operation.Parameters ?? [])
        {
            switch (parameter.Name.ToLowerInvariant())
            {
                case "pagesize":
                    parameter.Description = "Number of records to return (1-100; default 25).";
                    parameter.Schema = ReferenceSchema("PageSize");
                    break;
                case "cursor":
                    parameter.Description = "Opaque keyset cursor returned by the previous page.";
                    parameter.Schema = ReferenceSchema("PageCursor");
                    break;
                case "sort":
                    parameter.Description = "Allowlisted sort field.";
                    parameter.Schema ??= new OpenApiSchema { Type = "string" };
                    parameter.Schema.Enum =
                    [
                        new OpenApiString("createdAt"),
                        new OpenApiString("status"),
                        new OpenApiString("type"),
                        new OpenApiString("priority")
                    ];
                    break;
                case "direction":
                    parameter.Description = "Sort direction.";
                    parameter.Schema = ReferenceSchema("SortDirection");
                    break;
                case "limit":
                    parameter.Description = "Number of records to return (1-100; default 50 for conversations and 20 for notifications).";
                    parameter.Schema ??= new OpenApiSchema { Type = "integer", Format = "int32" };
                    parameter.Schema.Minimum = 1;
                    parameter.Schema.Maximum = 100;
                    break;
                case "beforeconversationid":
                case "beforesequence":
                case "aftersequence":
                    parameter.Description = "Opaque keyset boundary. Do not send mutually exclusive before and after boundaries together.";
                    break;
                case "status":
                    parameter.Description = "Filter by the resource lifecycle status (Ticket: Open or Resolved; Inquiry: Pending or Completed).";
                    break;
                case "type":
                    parameter.Description = "Filter by the public case type label defined by the endpoint's resource.";
                    break;
                case "priority":
                    parameter.Description = "Filter by Low, Normal, High, or Urgent.";
                    parameter.Schema ??= new OpenApiSchema { Type = "string" };
                    parameter.Schema.Enum =
                    [
                        new OpenApiString("Low"),
                        new OpenApiString("Normal"),
                        new OpenApiString("High"),
                        new OpenApiString("Urgent")
                    ];
                    break;
                case "clientid":
                    parameter.Description = "Admin-only tenant filter. Client tenant scope is always derived from trusted claims.";
                    break;
            }
        }
    }

    private static void AddConcurrencyMetadata(OpenApiOperation operation)
    {
        foreach (var schema in operation.RequestBody?.Content.Values
                     .Select(mediaType => mediaType.Schema)
                     .Where(schema => schema is not null)
                     .Select(schema => schema!) ?? [])
        {
            if (schema.Properties.ContainsKey("expectedVersion"))
            {
                schema.Properties["expectedVersion"] = ReferenceSchema("ConcurrencyToken");
            }
        }
    }

    private static void AddExpectedResponses(OpenApiOperation operation, string method, string path)
    {
        AddProblemResponse(operation, "400", "Request validation failed.");
        AddProblemResponse(operation, "401", "Authentication is required.");
        AddProblemResponse(operation, "403", "The authenticated principal is not authorized for this operation.");
        AddProblemResponse(operation, "404", "The resource does not exist or is not visible to the caller.");
        AddProblemResponse(operation, "500", "An unexpected server error occurred.");

        if (method is "POST" or "PUT" or "PATCH" or "DELETE")
        {
            AddProblemResponse(operation, "409", "The request conflicts with current resource state or its concurrency token.");
        }

        if (path.Contains("attachment-uploads", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/group", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/private", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
        {
            AddProblemResponse(operation, "429", "The caller exceeded the endpoint rate limit.");
        }

        if (method == "POST")
        {
            if (!operation.Responses.ContainsKey("201"))
            {
                operation.Responses["201"] = new OpenApiResponse
                {
                    Description = "Created.",
                    Content = operation.Responses.TryGetValue("200", out var ok)
                        ? ok.Content
                        : new Dictionary<string, OpenApiMediaType>()
                };
            }
        }
        else if (method == "DELETE"
            || (path.Contains("/conversations/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/read", StringComparison.OrdinalIgnoreCase)))
        {
            operation.Responses.Remove("200");
            operation.Responses.TryAdd("204", new OpenApiResponse { Description = "No content." });
        }

        if (path.EndsWith("/content", StringComparison.OrdinalIgnoreCase))
        {
            operation.Responses.Remove("200");
            operation.Responses.TryAdd("302", new OpenApiResponse
            {
                Description = "Redirect to the short-lived authorized attachment content URL."
            });
        }
    }

    private static void AddProblemResponse(OpenApiOperation operation, string statusCode, string description)
    {
        if (operation.Responses.ContainsKey(statusCode))
        {
            return;
        }

        operation.Responses[statusCode] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.Schema,
                            Id = "ProblemDetails"
                        }
                    }
                }
            }
        };
    }

    private static string GetTag(string path) => path switch
    {
        var value when value.Contains("tickets", StringComparison.OrdinalIgnoreCase) => "Tickets",
        var value when value.Contains("inquiries", StringComparison.OrdinalIgnoreCase) => "Inquiries",
        var value when value.Contains("conversations", StringComparison.OrdinalIgnoreCase) => "Conversations",
        var value when value.Contains("attachments", StringComparison.OrdinalIgnoreCase) => "Attachments",
        var value when value.Contains("notifications", StringComparison.OrdinalIgnoreCase) => "Notifications",
        _ => "API"
    };

    private static string CreateOperationId(string method, string path)
    {
        var suffix = string.Concat(path.Select(character =>
            char.IsLetterOrDigit(character) ? character : '_'));
        return $"{method.ToLowerInvariant()}_{suffix.Trim('_')}";
    }

    private static OpenApiSecurityScheme Reference(string id) =>
        new()
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = id }
        };

    private static OpenApiSchema ReferenceSchema(string id) =>
        new()
        {
            Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = id }
        };
}
