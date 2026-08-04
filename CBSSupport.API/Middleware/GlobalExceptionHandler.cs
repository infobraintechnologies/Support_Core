using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CBSSupport.API.Middleware;

/// <summary>
/// Production-safe global exception handler.
/// Returns RFC 7807 <see cref="ProblemDetails"/> for API/JSON requests and a generic
/// HTML error page for browser requests. Never exposes exception internals to callers.
/// The <see cref="HttpContext.TraceIdentifier"/> is written to the response and to the
/// structured log so a failure can be correlated with server-side logs.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private static readonly JsonSerializerOptions ProblemJsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = httpContext.TraceIdentifier;

        logger.LogError(
            exception,
            "Unhandled exception. TraceId: {TraceId}, Method: {Method}, Path: {Path}",
            traceId,
            httpContext.Request.Method,
            httpContext.Request.Path);

        try
        {
            if (httpContext.Response.HasStarted
                || (exception is OperationCanceledException
                    && httpContext.RequestAborted.IsCancellationRequested))
            {
                // Too late to write a body, or the caller already disconnected. Never loop.
                return true;
            }

            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

            if (WantsHtml(httpContext))
            {
                httpContext.Response.ContentType = "text/html; charset=utf-8";
                await httpContext.Response.WriteAsync(
                    BuildHtmlErrorPage(traceId),
                    Encoding.UTF8,
                    cancellationToken);
            }
            else
            {
                httpContext.Response.ContentType = "application/problem+json; charset=utf-8";
                var problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "An unexpected error occurred.",
                    Detail = "The request could not be completed. Please try again later.",
                    Instance = httpContext.Request.Path
                };
                problemDetails.Extensions["traceId"] = traceId;

                await JsonSerializer.SerializeAsync(
                    httpContext.Response.Body,
                    problemDetails,
                    ProblemJsonOptions,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller disconnected mid-write. Treat as handled to avoid a handler loop.
        }
        catch
        {
            // Writing the error response itself failed; swallow so the handler never loops.
        }

        return true;
    }

    private static bool WantsHtml(HttpContext httpContext)
    {
        var acceptedMediaTypes = httpContext.Request.GetTypedHeaders().Accept;
        if (acceptedMediaTypes is null)
        {
            return false;
        }

        foreach (var mediaType in acceptedMediaTypes)
        {
            if (string.Equals(
                mediaType.MediaType.Value,
                "text/html",
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildHtmlErrorPage(string traceId)
    {
        var escapedTraceId = WebUtility.HtmlEncode(traceId);
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            """
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Error - CBS Support</title>
            <style>
              body {{ font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif; margin: 0; background: #f5f6f8; color: #1b1f23; }}
              main {{ max-width: 40rem; margin: 4rem auto; padding: 2rem; background: #fff; border: 1px solid #d0d7de; border-radius: 8px; }}
              h1 {{ margin-top: 0; font-size: 1.4rem; }}
              p {{ line-height: 1.5; }}
              code {{ color: #57606a; }}
            </style>
            </head>
            <body>
            <main>
            <h1>An unexpected error occurred</h1>
            <p>The server could not complete your request. Please try again later.</p>
            <p>Error reference: <code>{0}</code></p>
            <p><a href="/Login">Return to sign in</a></p>
            </main>
            </body>
            </html>
            """,
            escapedTraceId);
    }
}