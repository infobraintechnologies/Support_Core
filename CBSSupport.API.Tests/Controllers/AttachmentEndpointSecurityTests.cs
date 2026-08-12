using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using CBSSupport.API.Security;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CBSSupport.API.Tests.Controllers;

public sealed partial class AttachmentEndpointSecurityTests
    : IClassFixture<AttachmentEndpointSecurityTests.AttachmentApplicationFactory>
{
    private const long AllowedClientId = 42;
    private const long OtherClientId = 99;
    private readonly AttachmentApplicationFactory _factory;
    private readonly HttpClient _client;

    public AttachmentEndpointSecurityTests(AttachmentApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Theory]
    [InlineData("POST", "/api/v1/conversations/25/attachment-uploads")]
    [InlineData("PUT", "/api/v1/attachments/8d887a42-bd86-45d3-a32e-fc67a3ea1550/upload")]
    [InlineData("DELETE", "/api/v1/attachments/8d887a42-bd86-45d3-a32e-fc67a3ea1550")]
    public async Task CookieAuthenticatedUnsafeAttachmentRequest_WithoutAntiforgery_Returns400(
        string method,
        string path)
    {
        var cookie = _factory.CreateAuthenticationCookie(AllowedClientId);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(
                new CreateAttachmentUploadRequest(
                    "report.pdf",
                    "application/pdf",
                    100));
        }
        else if (method == "PUT")
        {
            request.Content = new ByteArrayContent(new byte[100]);
            request.Content.Headers.ContentType = new("application/pdf");
        }
        var before = _factory.Service.TotalMutationCalls;

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, _factory.Service.TotalMutationCalls);
    }

    [Fact]
    public async Task CookieAuthenticatedPostPutAndDelete_WithAntiforgery_ReachAttachmentService()
    {
        var browser = await CreateBrowserSessionAsync(
            _factory,
            _client,
            AllowedClientId);
        var createCallsBefore = _factory.Service.CreateCalls;
        var uploadCallsBefore = _factory.Service.UploadCalls;
        var cancelCallsBefore = _factory.Service.CancelCalls;
        using var post = CreateUnsafeRequest(
            HttpMethod.Post,
            "/api/v1/conversations/25/attachment-uploads",
            browser,
            JsonContent.Create(
                new CreateAttachmentUploadRequest(
                    "report.pdf",
                    "application/pdf",
                    100)));
        using var postResponse = await _client.SendAsync(post);

        using var uploadContent = new ByteArrayContent(new byte[100]);
        uploadContent.Headers.ContentType = new("application/pdf");
        using var put = CreateUnsafeRequest(
            HttpMethod.Put,
            $"/api/v1/attachments/{FakeAttachmentService.ReadyId:D}/upload",
            browser,
            uploadContent);
        using var putResponse = await _client.SendAsync(put);

        using var delete = CreateUnsafeRequest(
            HttpMethod.Delete,
            $"/api/v1/attachments/{FakeAttachmentService.ReadyId:D}",
            browser);
        using var deleteResponse = await _client.SendAsync(delete);

        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);
        Assert.NotNull(putResponse.Headers.ETag);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Equal(createCallsBefore + 1, _factory.Service.CreateCalls);
        Assert.Equal(uploadCallsBefore + 1, _factory.Service.UploadCalls);
        Assert.Equal(cancelCallsBefore + 1, _factory.Service.CancelCalls);
    }

    [Fact]
    public async Task UploadDeclaration_UnsupportedTypeReturns415_ButRejectedScanIs200Status()
    {
        var browser = await CreateBrowserSessionAsync(
            _factory,
            _client,
            AllowedClientId);
        using var post = CreateUnsafeRequest(
            HttpMethod.Post,
            "/api/v1/conversations/25/attachment-uploads",
            browser,
            JsonContent.Create(
                new CreateAttachmentUploadRequest(
                    "payload.exe",
                    "application/octet-stream",
                    100)));
        using var declarationResponse = await _client.SendAsync(post);
        using var declarationProblem = JsonDocument.Parse(
            await declarationResponse.Content.ReadAsStringAsync());

        using var statusRequest = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/attachments/{FakeAttachmentService.RejectedId:D}",
            browser.AuthenticationCookie);
        using var statusResponse = await _client.SendAsync(statusRequest);
        using var statusJson = JsonDocument.Parse(
            await statusResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, declarationResponse.StatusCode);
        Assert.Equal(
            "attachment_type_unsupported",
            declarationProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.Equal(
            AttachmentStates.Rejected,
            statusJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            AttachmentRejectionCodes.ContentTypeMismatch,
            statusJson.RootElement.GetProperty("rejectionCode").GetString());
    }

    [Fact]
    public async Task ScannerUnavailable_Returns503WithRetryAfterAndStableCode()
    {
        var browser = await CreateBrowserSessionAsync(
            _factory,
            _client,
            AllowedClientId);
        using var post = CreateUnsafeRequest(
            HttpMethod.Post,
            "/api/v1/conversations/25/attachment-uploads",
            browser,
            JsonContent.Create(
                new CreateAttachmentUploadRequest(
                    "scanner-unavailable.pdf",
                    "application/pdf",
                    100)));

        using var response = await _client.SendAsync(post);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("23", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(CultureInfo.InvariantCulture)
            ?? Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal(
            "clamav_definitions_stale",
            json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OtherTenantReceives404AndStatusResponseContainsNoStorageFields()
    {
        var allowedCookie = _factory.CreateAuthenticationCookie(AllowedClientId);
        using var allowedRequest = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/attachments/{FakeAttachmentService.ReadyId:D}",
            allowedCookie);
        using var allowedResponse = await _client.SendAsync(allowedRequest);
        var allowedJsonText = await allowedResponse.Content.ReadAsStringAsync();

        var otherCookie = _factory.CreateAuthenticationCookie(OtherClientId);
        using var otherRequest = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/attachments/{FakeAttachmentService.ReadyId:D}",
            otherCookie);
        using var otherResponse = await _client.SendAsync(otherRequest);

        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherResponse.StatusCode);
        Assert.DoesNotContain("quarantine", allowedJsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("readyKey", allowedJsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("etag", allowedJsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256", allowedJsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uploadUrl", allowedJsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", allowedJsonText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OtherTenantCannotUploadBytesForAttachmentId()
    {
        var browser = await CreateBrowserSessionAsync(
            _factory,
            _client,
            OtherClientId);
        using var content = new ByteArrayContent(new byte[100]);
        content.Headers.ContentType = new("application/pdf");
        using var request = CreateUnsafeRequest(
            HttpMethod.Put,
            $"/api/v1/attachments/{FakeAttachmentService.ReadyId:D}/upload",
            browser,
            content);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ContentStreamRequiresReadyAuthorizedAttachment()
    {
        var allowedCookie = _factory.CreateAuthenticationCookie(AllowedClientId);
        using var readyRequest = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/attachments/{FakeAttachmentService.ReadyId:D}/content?disposition=inline",
            allowedCookie);
        using var readyResponse = await _client.SendAsync(readyRequest);

        using var rejectedRequest = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/attachments/{FakeAttachmentService.RejectedId:D}/content",
            allowedCookie);
        using var rejectedResponse = await _client.SendAsync(rejectedRequest);

        var otherCookie = _factory.CreateAuthenticationCookie(OtherClientId);
        using var otherTenantRequest = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/attachments/{FakeAttachmentService.ReadyId:D}/content",
            otherCookie);
        using var otherTenantResponse = await _client.SendAsync(otherTenantRequest);

        Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
        Assert.Equal("nosniff", readyResponse.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("application/pdf", readyResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", readyResponse.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(HttpStatusCode.NotFound, rejectedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherTenantResponse.StatusCode);
    }

    [Fact]
    public async Task FeatureDisabled_ConcealsAttachmentEndpointsAs404()
    {
        using var disabledFactory = new DisabledAttachmentApplicationFactory();
        using var client = disabledFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var browser = await CreateBrowserSessionAsync(
            disabledFactory,
            client,
            AllowedClientId);
        using var statusRequest = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/attachments/{FakeAttachmentService.ReadyId:D}",
            browser.AuthenticationCookie);
        using var statusResponse = await client.SendAsync(statusRequest);

        using var post = CreateUnsafeRequest(
            HttpMethod.Post,
            "/api/v1/conversations/25/attachment-uploads",
            browser,
            JsonContent.Create(
                new CreateAttachmentUploadRequest(
                    "report.pdf",
                    "application/pdf",
                    100)));
        using var postResponse = await client.SendAsync(post);

        Assert.Equal(HttpStatusCode.NotFound, statusResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, postResponse.StatusCode);
    }

    private static async Task<BrowserSession> CreateBrowserSessionAsync(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        long clientId)
    {
        var authenticationCookie = CreateAuthenticationCookie(factory, clientId);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            "/Support/Index",
            authenticationCookie);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var tokenMatch = AntiforgeryTokenPattern().Match(html);
        Assert.True(tokenMatch.Success, "The support view did not emit an antiforgery token.");
        var antiforgeryCookie = response.Headers
            .GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0])
            .Single(value => value.StartsWith(
                ".AspNetCore.Antiforgery.",
                StringComparison.Ordinal));
        return new(
            authenticationCookie,
            antiforgeryCookie,
            tokenMatch.Groups[1].Value);
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        string path,
        string authenticationCookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Cookie", authenticationCookie);
        return request;
    }

    private static HttpRequestMessage CreateUnsafeRequest(
        HttpMethod method,
        string path,
        BrowserSession browser,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{browser.AuthenticationCookie}; {browser.AntiforgeryCookie}");
        request.Headers.TryAddWithoutValidation(
            AntiforgeryConstants.HeaderName,
            browser.RequestToken);
        return request;
    }

    private static string CreateAuthenticationCookie(
        WebApplicationFactory<Program> factory,
        long clientId)
    {
        _ = factory.Server;
        using var scope = factory.Services.CreateScope();
        var cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "7"),
                new Claim(ClaimTypes.Name, "Client User"),
                new Claim(ClaimTypes.Role, Roles.Client),
                new Claim(CustomClaimTypes.ClientId, clientId.ToString(CultureInfo.InvariantCulture))
            ],
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties
        {
            IssuedUtc = now,
            ExpiresUtc = now.AddHours(1)
        };
        properties.Items[CookiePrincipalValidationEvents.LastValidatedUtcProperty] =
            now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            properties,
            CookieAuthenticationDefaults.AuthenticationScheme);
        return $"{cookieOptions.Cookie.Name}={cookieOptions.TicketDataFormat.Protect(ticket)}";
    }

    [GeneratedRegex(
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryTokenPattern();

    private sealed record BrowserSession(
        string AuthenticationCookie,
        string AntiforgeryCookie,
        string RequestToken);

    public sealed class AttachmentApplicationFactory : WebApplicationFactory<Program>
    {
        public FakeAttachmentService Service { get; } = new();

        public string CreateAuthenticationCookie(long clientId) =>
            AttachmentEndpointSecurityTests.CreateAuthenticationCookie(this, clientId);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ConfigureTestHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAttachmentService>();
                services.AddSingleton<IAttachmentService>(Service);
            });
        }
    }

    private sealed class DisabledAttachmentApplicationFactory
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            ConfigureTestHost(builder);
    }

    private static void ConfigureTestHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.UseEnvironment("Testing");
        builder.UseSetting(
            "ConnectionStrings:DefaultConnection",
            "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
        builder.UseSetting("Jwt:Enabled", "false");
        builder.UseSetting("Security:PasswordHashing:Pepper", "test-company-pepper");
        builder.UseSetting("Attachments:Enabled", "false");
    }

    public sealed class FakeAttachmentService : IAttachmentService
    {
        public static readonly Guid ReadyId = Guid.Parse(
            "8d887a42-bd86-45d3-a32e-fc67a3ea1550");
        public static readonly Guid RejectedId = Guid.Parse(
            "2cbfc79f-9d3b-4300-9e65-d56f032ea44c");
        private int _createCalls;
        private int _uploadCalls;
        private int _cancelCalls;

        public int CreateCalls => _createCalls;
        public int UploadCalls => _uploadCalls;
        public int CancelCalls => _cancelCalls;
        public int TotalMutationCalls => CreateCalls + UploadCalls + CancelCalls;

        public Task<AttachmentCommandResult<AttachmentUploadIntent>> CreateUploadIntentAsync(
            long conversationId,
            AttachmentActor actor,
            CreateAttachmentUploadRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _createCalls);
            if (actor.ClientId != AllowedClientId)
            {
                return Task.FromResult(
                    new AttachmentCommandResult<AttachmentUploadIntent>(
                        AttachmentCommandStatus.Unavailable,
                        ErrorCode: "conversation_unavailable"));
            }
            if (request.DisplayName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(
                    new AttachmentCommandResult<AttachmentUploadIntent>(
                        AttachmentCommandStatus.Unsupported,
                        ErrorCode: "attachment_type_unsupported"));
            }
            if (request.DisplayName == "scanner-unavailable.pdf")
            {
                return Task.FromResult(
                    new AttachmentCommandResult<AttachmentUploadIntent>(
                        AttachmentCommandStatus.ScannerUnavailable,
                        ErrorCode: "clamav_definitions_stale",
                        RetryAfterSeconds: 23));
            }
            return Task.FromResult(
                new AttachmentCommandResult<AttachmentUploadIntent>(
                    AttachmentCommandStatus.Accepted,
                    new AttachmentUploadIntent(
                        ReadyId,
                        $"/api/v1/attachments/{ReadyId:D}/upload",
                        DateTimeOffset.UtcNow.AddMinutes(5),
                        new Dictionary<string, string>
                        {
                            ["Content-Type"] = "application/pdf"
                        },
                        new AttachmentSummary(
                            ReadyId,
                            request.DisplayName,
                            request.MediaType,
                            request.Size,
                            AttachmentStates.PendingUpload,
                            null))));
        }

        public Task<AttachmentCommandResult<AttachmentStatusResponse>> CompleteAsync(
            Guid attachmentId,
            AttachmentActor actor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                actor.ClientId == AllowedClientId
                    ? new AttachmentCommandResult<AttachmentStatusResponse>(
                        AttachmentCommandStatus.Accepted,
                        CreateStatus(attachmentId, AttachmentStates.Uploaded))
                    : new AttachmentCommandResult<AttachmentStatusResponse>(
                        AttachmentCommandStatus.Unavailable,
                        ErrorCode: "attachment_not_found"));

        public Task<AttachmentCommandResult<StoredObjectInfo>> UploadAsync(
            Guid attachmentId,
            AttachmentActor actor,
            Stream content,
            string? mediaType,
            long? contentLength,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _uploadCalls);
            return Task.FromResult(
                actor.ClientId == AllowedClientId && attachmentId == ReadyId
                    ? new AttachmentCommandResult<StoredObjectInfo>(
                        AttachmentCommandStatus.Success,
                        new StoredObjectInfo(
                            $"{attachmentId:D}.pending.pdf",
                            contentLength ?? 0,
                            "test-etag",
                            mediaType,
                            new Dictionary<string, string>()))
                    : new AttachmentCommandResult<StoredObjectInfo>(
                        AttachmentCommandStatus.Unavailable,
                        ErrorCode: "attachment_not_found"));
        }

        public Task<AttachmentStatusResponse?> GetStatusAsync(
            Guid attachmentId,
            AttachmentActor actor,
            CancellationToken cancellationToken = default)
        {
            if (actor.ClientId != AllowedClientId)
            {
                return Task.FromResult<AttachmentStatusResponse?>(null);
            }
            return Task.FromResult<AttachmentStatusResponse?>(
                attachmentId == ReadyId
                    ? CreateStatus(attachmentId, AttachmentStates.Ready)
                    : attachmentId == RejectedId
                        ? CreateStatus(
                            attachmentId,
                            AttachmentStates.Rejected,
                            AttachmentRejectionCodes.ContentTypeMismatch)
                        : null);
        }

        public Task<AttachmentCommandResult<bool>> CancelAsync(
            Guid attachmentId,
            AttachmentActor actor,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _cancelCalls);
            return Task.FromResult(
                actor.ClientId == AllowedClientId
                    ? new AttachmentCommandResult<bool>(
                        AttachmentCommandStatus.Accepted,
                        true)
                    : new AttachmentCommandResult<bool>(
                        AttachmentCommandStatus.Unavailable,
                        ErrorCode: "attachment_not_found"));
        }

        public Task<AttachmentCommandResult<AttachmentContentRead>> OpenContentAsync(
            Guid attachmentId,
            AttachmentActor actor,
            string disposition,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                actor.ClientId == AllowedClientId && attachmentId == ReadyId
                    ? new AttachmentCommandResult<AttachmentContentRead>(
                        AttachmentCommandStatus.Success,
                        new AttachmentContentRead(
                            new MemoryStream("%PDF-test"u8.ToArray()),
                            "report.pdf",
                            "application/pdf",
                            "attachment"))
                    : new AttachmentCommandResult<AttachmentContentRead>(
                        AttachmentCommandStatus.Unavailable,
                        ErrorCode: "attachment_not_found"));

        private static AttachmentStatusResponse CreateStatus(
            Guid id,
            string status,
            string? rejectionCode = null) =>
            new(
                id,
                25,
                "report.pdf",
                "application/pdf",
                100,
                status,
                rejectionCode,
                DateTimeOffset.UtcNow,
                status == AttachmentStates.Ready ? DateTimeOffset.UtcNow : null,
                DateTimeOffset.UtcNow.AddDays(1));
    }
}
