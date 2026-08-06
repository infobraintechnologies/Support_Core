using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace CBSSupport.API.OpenApi;

/// <summary>
/// Applies the stable, public contract metadata shared by the versioned API.
/// The endpoint filter is configured in Program.cs; this transformer only adds
/// document-level metadata and reusable public schemas.
/// </summary>
public sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "CBS Support API",
            Version = "v1",
            Description = "Versioned support and ticketing API. All operations require an authenticated Client or Admin principal unless stated otherwise. Client operations are tenant-scoped from trusted claims; Admin operations require the Admin policy and selected-tenant authorization where applicable."
        };

        document.Components ??= new OpenApiComponents();
        AddSecuritySchemes(document.Components);
        AddPublicSchemas(document.Components);

        document.SecurityRequirements =
        [
            new OpenApiSecurityRequirement
            {
                [Reference("cookieAuth", ReferenceType.SecurityScheme)] = []
            },
            new OpenApiSecurityRequirement
            {
                [Reference("bearerAuth", ReferenceType.SecurityScheme)] = []
            }
        ];

        return Task.CompletedTask;
    }

    private static void AddSecuritySchemes(OpenApiComponents components)
    {
        components.SecuritySchemes["cookieAuth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = "CBSSupport.AuthCookie",
            Description = "Browser session cookie. Unsafe browser mutations also require the RequestVerificationToken header."
        };
        components.SecuritySchemes["bearerAuth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Optional JWT bearer authentication for explicitly enabled external API clients."
        };
        components.SecuritySchemes["csrfToken"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "RequestVerificationToken",
            Description = "Required for unsafe cookie-authenticated browser requests."
        };
    }

    private static void AddPublicSchemas(OpenApiComponents components)
    {
        components.Schemas["ProblemDetails"] = new OpenApiSchema
        {
            Type = "object",
            Description = "RFC 7807 problem response.",
            AdditionalPropertiesAllowed = true,
            Properties = new Dictionary<string, OpenApiSchema>
            {
                ["type"] = new() { Type = "string", Format = "uri" },
                ["title"] = new() { Type = "string" },
                ["status"] = new() { Type = "integer", Format = "int32" },
                ["detail"] = new() { Type = "string" },
                ["instance"] = new() { Type = "string", Format = "uri-reference" },
                ["traceId"] = new() { Type = "string" },
                ["code"] = new() { Type = "string" }
            }
        };
        components.Schemas["PageSize"] = new OpenApiSchema
        {
            Type = "integer",
            Format = "int32",
            Minimum = 1,
            Maximum = 100,
            Default = new OpenApiInteger(25),
            Description = "Number of records to return. The server enforces a maximum of 100."
        };
        components.Schemas["PageCursor"] = new OpenApiSchema
        {
            Type = "string",
            MaxLength = 1024,
            Description = "Opaque keyset cursor returned by the previous page. Do not inspect or modify it."
        };
        components.Schemas["SortDirection"] = new OpenApiSchema
        {
            Type = "string",
            Enum = [new OpenApiString("asc"), new OpenApiString("desc")],
            Default = new OpenApiString("desc")
        };
        components.Schemas["ConcurrencyToken"] = new OpenApiSchema
        {
            Type = "integer",
            Format = "int64",
            Minimum = 1,
            Description = "Optimistic-concurrency version returned by the resource and sent back on a mutation as expectedVersion."
        };
    }

    private static OpenApiSecurityScheme Reference(string id, ReferenceType type) =>
        new()
        {
            Reference = new OpenApiReference { Type = type, Id = id }
        };
}
