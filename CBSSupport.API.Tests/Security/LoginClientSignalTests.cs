using System.Net;
using CBSSupport.API.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CBSSupport.API.Tests.Security;

public sealed class LoginClientSignalTests
{
    [Fact]
    public async Task TrustedProxy_ForwardedForBecomesTheClientSignal()
    {
        var proxy = IPAddress.Parse("192.0.2.10");
        using var server = CreateServer(proxy);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.10");

        var response = await client.GetStringAsync("/");

        Assert.Equal("198.51.100.10", response);
    }

    [Fact]
    public async Task UntrustedProxy_ForwardedForIsIgnored()
    {
        var proxy = IPAddress.Parse("192.0.2.11");
        using var server = CreateServer(IPAddress.Parse("192.0.2.10"), proxy);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.10");

        var response = await client.GetStringAsync("/");

        Assert.Equal(proxy.ToString(), response);
    }

    private static WebApplication CreateServer(IPAddress trustedProxy, IPAddress? actualProxy = null)
    {
        actualProxy ??= trustedProxy;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.KnownProxies.Add(trustedProxy);
        });

        var application = builder.Build();
        application.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = actualProxy;
            await next();
        });
        application.UseForwardedHeaders();
        application.Run(context => context.Response.WriteAsync(
            LoginAccountKey.ClientSignal(context.Connection.RemoteIpAddress)));
        application.Start();
        return application;
    }
}
