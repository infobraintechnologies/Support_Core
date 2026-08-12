using Microsoft.AspNetCore.Http.Features;

namespace CBSSupport.API.Security;

public sealed class RequestSizeLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var limit = IsAttachmentByteUpload(context.Request)
            ? RequestSizeLimits.MaximumAttachmentBodySizeBytes
            : RequestSizeLimits.MaximumBodySizeBytes;
        var maxRequestBodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxRequestBodySize is { IsReadOnly: false })
        {
            maxRequestBodySize.MaxRequestBodySize = limit;
        }

        if (context.Request.ContentLength > limit)
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

    private static bool IsAttachmentByteUpload(HttpRequest request)
    {
        if (!HttpMethods.IsPut(request.Method))
        {
            return false;
        }

        var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["api", "v1", "attachments", var attachmentId, "upload"]
            && Guid.TryParse(attachmentId, out _);
    }
}
