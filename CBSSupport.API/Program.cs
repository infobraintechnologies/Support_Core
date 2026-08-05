using CBSSupport.API.Hubs;
using CBSSupport.API.Controllers;
using CBSSupport.API.Middleware;
using CBSSupport.API.Realtime;
using CBSSupport.API.Configuration;
using CBSSupport.API.Security;
using CBSSupport.API.Attachments;
using CBSSupport.Shared.Data;
using CBSSupport.Shared.Helpers;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation;
using Microsoft.AspNetCore.Server.IIS;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = RequestSizeLimits.MaximumBodySizeBytes);
builder.Services.Configure<IISServerOptions>(options =>
    options.MaxRequestBodySize = RequestSizeLimits.MaximumBodySizeBytes);
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = RequestSizeLimits.MaximumBodySizeBytes);

var mvcBuilder = builder.Services.AddControllersWithViews(SecurityMvcOptions.ConfigureMvc);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddAntiforgery(SecurityMvcOptions.ConfigureAntiforgery);

if (builder.Environment.IsDevelopment())
{
    mvcBuilder.AddRazorRuntimeCompilation();
}


builder.Services.AddSignalR(options =>
{
    options.AddFilter(typeof(HubPrincipalValidationFilter));
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 16 * 1024;
    options.MaximumParallelInvocationsPerClient = 1;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var configuredProxy in builder.Configuration.GetSection("Security:KnownProxies").Get<string[]>() ?? [])
    {
        if (!IPAddress.TryParse(configuredProxy, out var proxyAddress))
        {
            throw new InvalidOperationException(
                $"Security:KnownProxies contains invalid IP address '{configuredProxy}'.");
        }

        options.KnownProxies.Add(proxyAddress);
    }
});

var loginSecurityOptions = builder.Configuration
    .GetSection(LoginSecurityOptions.SectionName)
    .Get<LoginSecurityOptions>() ?? new LoginSecurityOptions();
loginSecurityOptions.Validate();
var jwtSecurityOptions = builder.Configuration
    .GetSection(JwtSecurityOptions.SectionName)
    .Get<JwtSecurityOptions>() ?? new JwtSecurityOptions();
jwtSecurityOptions.Validate();

builder.Services.AddSingleton(loginSecurityOptions);
builder.Services.AddSingleton(jwtSecurityOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ILoginAttemptLimiter, LoginAttemptLimiter>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(LoginRateLimitPolicies.PerIp, httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = loginSecurityOptions.PerIpPermitLimit,
                Window = loginSecurityOptions.PerIpWindow,
                SegmentsPerWindow = loginSecurityOptions.PerIpSegments,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy(MessagingRateLimitPolicies.MessageSend, httpContext =>
        RateLimitPartition.GetTokenBucketLimiter(
            GetMessagingRateLimitKey(httpContext),
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 10,
                TokensPerPeriod = 30,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy(MessagingRateLimitPolicies.ConversationCreation, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            GetMessagingRateLimitKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds)
                .ToString(CultureInfo.InvariantCulture);
        }

        if (context.HttpContext.Request.Headers.Accept.ToString()
            .Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            context.HttpContext.Response.ContentType = "text/html; charset=utf-8";
            await context.HttpContext.Response.WriteAsync(
                """
                <!doctype html>
                <html lang="en">
                <head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Too many login attempts</title></head>
                <body><main><h1>Too many login attempts</h1><p>Wait before trying to sign in again.</p><p><a href="/Login">Return to sign in</a></p></main></body>
                </html>
                """,
                cancellationToken);
            return;
        }

        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many login attempts",
                Detail = "Wait before trying to sign in again."
            },
            typeof(ProblemDetails),
            cancellationToken);
    };
});

// --- 2. Get Connection String ---
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrEmpty(connectionString))
{
    throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
}

// --- 3. Register your custom services---
var attachmentOptions = builder.Configuration
    .GetSection(AttachmentOptions.SectionName)
    .Get<AttachmentOptions>() ?? new AttachmentOptions();
attachmentOptions.Validate();
builder.Services.AddSingleton(attachmentOptions);
builder.Services.AddSingleton<IChatService>(provider => new ChatService(
    connectionString,
    provider.GetRequiredService<ILogger<ChatService>>()));
builder.Services.AddSingleton<IConversationQueryService>(provider => new ConversationQueryService(
    connectionString,
    provider.GetRequiredService<ILogger<ConversationQueryService>>()));
builder.Services.AddSingleton<ICaseMutationCommandHandler>(_ => new CaseMutationCommandHandler(connectionString));
builder.Services.AddSingleton<ITicketService, TicketService>();
builder.Services.AddSingleton<IInquiryService, InquiryService>();
builder.Services.AddSingleton<IConversationRepository>(
    new ConversationRepository(connectionString, attachmentOptions.Enabled));
builder.Services.AddSingleton<IConversationOutboxRepository>(
    new ConversationOutboxRepository(connectionString, attachmentOptions.Enabled));
builder.Services.AddSingleton<IConversationService, ConversationService>();
builder.Services.AddSingleton<IAttachmentRepository>(
    new AttachmentRepository(connectionString));
if (attachmentOptions.SecurityMode == AttachmentSecurityMode.MalwareScanning)
{
    builder.Services.AddSingleton<IFileScanner>(provider => new ClamAvFileScanner(
        attachmentOptions.Scanning,
        provider.GetRequiredService<TimeProvider>()));
}
builder.Services.AddSingleton(provider => new AttachmentUiCapability(
    attachmentOptions,
    provider.GetService<IFileScanner>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IFileStorage>(_ =>
    attachmentOptions.Enabled
        ? new R2FileStorage(attachmentOptions.R2)
        : new DisabledFileStorage());
builder.Services.AddSingleton<IAttachmentService>(provider => new AttachmentService(
    provider.GetRequiredService<IAttachmentRepository>(),
    provider.GetRequiredService<IConversationService>(),
    provider.GetRequiredService<IFileStorage>(),
    provider.GetService<IFileScanner>(),
    attachmentOptions,
    provider.GetRequiredService<TimeProvider>()));
if (attachmentOptions.Enabled)
{
    builder.Services.AddHostedService<AttachmentCleanupWorker>();
    if (attachmentOptions.SecurityMode == AttachmentSecurityMode.StructuralValidationOnly)
    {
        builder.Services.AddHostedService<AttachmentValidationWorker>();
    }
    else
    {
        builder.Services.AddHostedService<ClamAvHealthMonitor>();
        if (attachmentOptions.Scanning.WorkerEnabled)
        {
            builder.Services.AddHostedService<AttachmentScanWorker>();
        }
    }
}
builder.Services.Configure<MessagingFeatureOptions>(
    builder.Configuration.GetSection(MessagingFeatureOptions.SectionName));
builder.Services.AddSingleton<IUserIdProvider, NamespacedUserIdProvider>();
builder.Services.AddSingleton<IConversationRealtimePublisher, SignalRConversationRealtimePublisher>();
builder.Services.Configure<ConversationOutboxDispatcherOptions>(
    builder.Configuration.GetSection("Messaging:Outbox"));
builder.Services.AddHostedService<ConversationOutboxDispatcher>();
builder.Services.AddSingleton<IUserRepository>(provider => new UserRepository(connectionString));
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IAccountSecurityStampService, DataProtectionAccountSecurityStampService>();
builder.Services.AddSingleton<IAccountPrincipalValidator, AccountPrincipalValidator>();
builder.Services.AddScoped<CookiePrincipalValidationEvents>();
builder.Services.AddScoped<JwtPrincipalValidationEvents>();
builder.Services.AddSingleton<IActiveHubConnectionRegistry, ActiveHubConnectionRegistry>();
builder.Services.AddSingleton<HubPrincipalValidationFilter>();
builder.Services.AddHostedService<HubConnectionRevocationMonitor>();
builder.Services.AddSingleton<IAuthorizationHandler, TenantAccessHandler>();

// --- 4. CONFIGURE AUTHENTICATION ---
var authenticationBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "CBSSupport.AuthCookie";
    options.LoginPath = "/Login/Index";
    options.LogoutPath = "/Login/Logout";
    options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
    options.SlidingExpiration = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.EventsType = typeof(CookiePrincipalValidationEvents);
});

if (jwtSecurityOptions.Enabled)
{
    authenticationBuilder.AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.SaveToken = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSecurityOptions.Issuer,
            ValidAudience = jwtSecurityOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecurityOptions.Key!)),
            NameClaimType = JwtClaimTypes.Name,
            RoleClaimType = JwtClaimTypes.Role,
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        options.EventsType = typeof(JwtPrincipalValidationEvents);
    });
}

var supportedAuthenticationSchemes = jwtSecurityOptions.Enabled
    ? new[] { CookieAuthenticationDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme }
    : [CookieAuthenticationDefaults.AuthenticationScheme];

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.AdminOnly, policy =>
    {
        policy.AddAuthenticationSchemes(supportedAuthenticationSchemes);
        policy.RequireAuthenticatedUser();
        policy.RequireRole(Roles.Admin);
        policy.RequireAssertion(context => context.User.TryGetUserId(out _));
    });

    options.AddPolicy(Policies.ClientOnly, policy =>
    {
        policy.AddAuthenticationSchemes(supportedAuthenticationSchemes);
        policy.RequireAuthenticatedUser();
        policy.RequireRole(Roles.Client);
        policy.RequireAssertion(context =>
            context.User.TryGetUserId(out _) && context.User.TryGetClientId(out _));
    });

    options.AddPolicy(Policies.AdminOrClient, policy =>
    {
        policy.AddAuthenticationSchemes(supportedAuthenticationSchemes);
        policy.RequireAuthenticatedUser();
        policy.RequireRole(Roles.Admin, Roles.Client);
        policy.RequireAssertion(context =>
            context.User.TryGetUserId(out _)
            && (!context.User.IsInRole(Roles.Client) || context.User.TryGetClientId(out _)));
    });
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseMiddleware<RequestSizeLimitMiddleware>();
app.UseMiddleware<AttachmentContainmentMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler();
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
// Messaging partitions depend on authenticated user and tenant claims.
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Login}/{action=Index}/{id?}");

app.MapControllers();
app.MapHub<ChatHub>("/chathub");
app.Run();

static string GetMessagingRateLimitKey(HttpContext context)
{
    var role = context.User.IsInRole(Roles.Admin) ? "admin" : "client";
    var userId = context.User.TryGetUserId(out var parsedUserId)
        ? parsedUserId.ToString(CultureInfo.InvariantCulture)
        : "anonymous";
    var clientId = context.User.TryGetClientId(out var parsedClientId)
        ? parsedClientId.ToString(CultureInfo.InvariantCulture)
        : "global";
    return $"{role}:{clientId}:{userId}";
}

public partial class Program;
