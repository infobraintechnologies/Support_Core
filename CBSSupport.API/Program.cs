using CBSSupport.API.Hubs;
using CBSSupport.API.Security;
using CBSSupport.Shared.Data;
using CBSSupport.Shared.Helpers;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var mvcBuilder = builder.Services.AddControllersWithViews(SecurityMvcOptions.ConfigureMvc);
builder.Services.AddAntiforgery(SecurityMvcOptions.ConfigureAntiforgery);

if (builder.Environment.IsDevelopment())
{
    mvcBuilder.AddRazorRuntimeCompilation();
}


builder.Services.AddSignalR(options =>
    options.AddFilter(typeof(HubPrincipalValidationFilter)));

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
builder.Services.AddSingleton<IChatService>(provider => new ChatService(connectionString));
builder.Services.AddSingleton<IConversationRepository>(
    new ConversationRepository(connectionString));
builder.Services.AddSingleton<IConversationService, ConversationService>();
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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Login}/{action=Index}/{id?}");

app.MapControllers();
app.MapHub<ChatHub>("/chathub");
app.Run();

public partial class Program;
