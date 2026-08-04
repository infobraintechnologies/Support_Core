using CBSSupport.API.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;

namespace CBSSupport.API.Tests.Controllers;

public sealed class AttachmentContainmentEndpointTests
    : IClassFixture<AttachmentContainmentEndpointTests.TestApplicationFactory>
{
    private readonly HttpClient _client;

    public AttachmentContainmentEndpointTests(TestApplicationFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Theory]
    [InlineData("/api/FileUpload/UploadFile")]
    [InlineData("/uploads/legacy-file.pdf")]
    public async Task LegacyAttachmentPath_Returns404(string path)
    {
        using var response = await _client.PostAsync(path, new ByteArrayContent([1]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RequestBodyOverGlobalLimit_Returns413()
    {
        using var content = new ByteArrayContent(
            new byte[checked((int)RequestSizeLimits.MaximumBodySizeBytes + 1)]);

        using var response = await _client.PostAsync("/Login/Index", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    public sealed class TestApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting(
                "ConnectionStrings:DefaultConnection",
                "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("Jwt:Enabled", "false");
        }
    }
}
