using Microsoft.AspNetCore.Http.Features;

namespace CBSSupport.API.Security;

public sealed class RequestSizeLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var maxRequestBodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxRequestBodySize is { IsReadOnly: false })
        {
            maxRequestBodySize.MaxRequestBodySize = RequestSizeLimits.MaximumBodySizeBytes;
        }

        if (context.Request.ContentLength > RequestSizeLimits.MaximumBodySizeBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(
                """
                {"type":"https://support.example/problems/request-too-large","title":"Request too large","status":413,"detail":"The request body exceeds the allowed size."}
                """,
                context.RequestAborted);
            return;
        }

        await next(context);
    }
}
