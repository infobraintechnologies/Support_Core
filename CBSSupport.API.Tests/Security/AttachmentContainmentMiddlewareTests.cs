using CBSSupport.API.Security;
using Microsoft.AspNetCore.Http;

namespace CBSSupport.API.Tests.Security;

public sealed class AttachmentContainmentMiddlewareTests
{
    [Theory]
    [InlineData("/api/FileUpload/UploadFile")]
    [InlineData("/uploads/legacy-file.pdf")]
    public async Task InvokeAsync_LegacyAttachmentPath_Returns404WithoutCallingNext(string path)
    {
        var nextCalled = false;
        var middleware = new AttachmentContainmentMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_UnrelatedPath_CallsNext()
    {
        var nextCalled = false;
        var middleware = new AttachmentContainmentMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/images/logo.png";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }
}
