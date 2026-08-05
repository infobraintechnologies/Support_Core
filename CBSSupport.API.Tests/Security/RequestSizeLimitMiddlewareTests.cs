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
}
