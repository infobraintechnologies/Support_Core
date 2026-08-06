using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CBSSupport.API.Tests.Controllers;

/// <summary>
/// Production-like integration tests for the centralized exception handler.
/// The test host runs the real pipeline in a non-development environment so the
/// <c>UseExceptionHandler()</c> branch is active; a fault-injecting test controller
/// triggers an unhandled exception inside MVC.
/// </summary>
public sealed class GlobalExceptionHandlerTests
{
    private const string SensitiveMarker = FaultInjectingController.SensitiveMarker;
    private const string ExceptionTypeName = "System.InvalidOperationException";

    [Fact]
    public async Task ApiRequest_UnhandledException_ReturnsProblemJsonWithTraceIdAndNoInternals()
    {
        using var factory = new ProductionLikeApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        using var response = await client.GetAsync("/api/test/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred.", root.GetProperty("title").GetString());
        Assert.Equal("/api/test/boom", root.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));

        Assert.DoesNotContain(SensitiveMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ExceptionTypeName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiRequest_UnhandledException_IsLoggedExactlyOnce()
    {
        using var factory = new ProductionLikeApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        using var response = await client.GetAsync("/api/test/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var handlerEntries = factory.LogEntries
            .Where(entry => entry.Message.Contains(
                "Unhandled exception. TraceId:",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Single(handlerEntries);
        Assert.Contains(SensitiveMarker, handlerEntries[0].ExceptionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HtmlRequest_UnhandledException_ReturnsGenericErrorPageNotBroken404()
    {
        using var factory = new ProductionLikeApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");

        using var response = await client.GetAsync("/api/test/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("An unexpected error occurred", body, StringComparison.Ordinal);
        Assert.Contains("Error reference:", body, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ExceptionTypeName, body, StringComparison.Ordinal);
        Assert.DoesNotContain("404", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DevelopmentRequest_UnhandledException_ShowsDeveloperExceptionPage()
    {
        using var factory = new DevelopmentApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/api/test/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(SensitiveMarker, body, StringComparison.Ordinal);
        Assert.Contains(ExceptionTypeName, body, StringComparison.Ordinal);
    }

    private sealed class ProductionLikeApplicationFactory : WebApplicationFactory<Program>
    {
        public List<RecordedLogEntry> LogEntries { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting(
                "ConnectionStrings:DefaultConnection",
                "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("Jwt:Enabled", "false");
            builder.UseSetting("Security:PasswordHashing:Pepper", "test-company-pepper");
            builder.ConfigureTestServices(services =>
            {
                RegisterFaultInjectingController(services);
                services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(
                    new RecordingLoggerFactory(LogEntries)));
            });
        }
    }

    private sealed class DevelopmentApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting(
                "ConnectionStrings:DefaultConnection",
                "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("Jwt:Enabled", "false");
            builder.UseSetting("Security:PasswordHashing:Pepper", "test-company-pepper");
            builder.UseSetting("Messaging:Features:PrivateEnabled", "false");
            builder.ConfigureTestServices(RegisterFaultInjectingController);
        }
    }

    private static void RegisterFaultInjectingController(IServiceCollection services) =>
        services.AddControllers().AddApplicationPart(typeof(FaultInjectingController).Assembly);

    private sealed record RecordedLogEntry(string Message, string ExceptionMessage);

    private sealed class RecordingLoggerFactory(List<RecordedLogEntry> logEntries) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) =>
            new RecordingLogger(logEntries);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(List<RecordedLogEntry> logEntries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                lock (logEntries)
                {
                    logEntries.Add(new RecordedLogEntry(
                        formatter(state, exception),
                        exception?.Message ?? string.Empty));
                }
            }
        }
    }
}

[ApiController]
[Route("api/test")]
public sealed class FaultInjectingController : ControllerBase
{
    public const string SensitiveMarker = "sensitive-internal-detail-connection-string=secret";

    [HttpGet("boom")]
    public IActionResult Boom() =>
        throw new InvalidOperationException(SensitiveMarker);
}
