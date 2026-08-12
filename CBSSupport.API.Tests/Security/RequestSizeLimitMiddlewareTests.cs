using CBSSupport.API.Security;
using Microsoft.AspNetCore.Http;

namespace CBSSupport.API.Tests.Security;

public sealed class RequestSizeLimitMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ContentLengthExceedsLimit_Returns413WithoutCallingNext()
    {
        var nextCalled = false;
        var middleware = new RequestSizeLimitMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.ContentLength = RequestSizeLimits.MaximumBodySizeBytes + 1;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
    }

    [Fact]
    public async Task InvokeAsync_ContentLengthAtLimit_CallsNext()
    {
        var nextCalled = false;
        var middleware = new RequestSizeLimitMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.ContentLength = RequestSizeLimits.MaximumBodySizeBytes;

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData(RequestSizeLimits.MaximumAttachmentBodySizeBytes, true)]
    [InlineData(RequestSizeLimits.MaximumAttachmentBodySizeBytes + 1, false)]
    public async Task InvokeAsync_AttachmentPut_UsesDedicatedTenMiBLimit(
        long contentLength,
        bool expectedNextCalled)
    {
        var nextCalled = false;
        var middleware = new RequestSizeLimitMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Put;
        context.Request.Path =
            "/api/v1/attachments/53684801-bcd9-4841-96ad-7bae78c22084/upload";
        context.Request.ContentLength = contentLength;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(expectedNextCalled, nextCalled);
        Assert.Equal(
            expectedNextCalled ? StatusCodes.Status200OK : StatusCodes.Status413PayloadTooLarge,
            context.Response.StatusCode);
    }
}
