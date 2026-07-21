using System.Reflection;
using System.Security.Claims;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Controllers;

public sealed class SupportControllerTests
{
    [Fact]
    public void Controller_UsesClientOnlyPolicy()
    {
        var attribute = Assert.Single(
            typeof(SupportController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
                .Cast<AuthorizeAttribute>());

        Assert.Equal(Policies.ClientOnly, attribute.Policy);
    }

    [Fact]
    public async Task Index_CanonicalClientClaim_UsesClaimTenantForDashboardBootstrap()
    {
        var chatService = DispatchProxy.Create<IChatService, RecordingChatServiceProxy>();
        var recordingService = (RecordingChatServiceProxy)(object)chatService;
        recordingService.ReturnValues[nameof(IChatService.GetOrCreateGroupChatConversationIdAsync)] =
            Task.FromResult(123L);
        var controller = new SupportController(
            NullLogger<SupportController>.Instance,
            chatService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateClientPrincipal()
                }
            }
        };

        var result = await controller.Index();

        Assert.IsType<ViewResult>(result);
        Assert.Equal(42L, controller.ViewBag.ClientId);
        Assert.Equal(7L, controller.ViewBag.UserId);
        Assert.Equal(123L, controller.ViewBag.GroupChatId);
        var call = Assert.Single(recordingService.Calls);
        Assert.Equal(nameof(IChatService.GetOrCreateGroupChatConversationIdAsync), call.MethodName);
        Assert.Equal(42L, call.Arguments[0]);
        Assert.Equal(7, call.Arguments[1]);
    }

    private static ClaimsPrincipal CreateClientPrincipal()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "7"),
                new Claim(ClaimTypes.Role, Roles.Client),
                new Claim(CustomClaimTypes.ClientId, "42"),
                new Claim("FullName", "Test Client")
            ],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}
