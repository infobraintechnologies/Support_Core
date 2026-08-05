namespace CBSSupport.API.Security;

public sealed class AttachmentContainmentMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/FileUpload")
            || context.Request.Path.StartsWithSegments("/uploads"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}
