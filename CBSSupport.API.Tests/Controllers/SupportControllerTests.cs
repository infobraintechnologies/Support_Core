using System.Security.Claims;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
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
    public void Index_CanonicalClientClaim_UsesClaimTenantWithoutCreatingConversation()
    {
        var controller = new SupportController(NullLogger<SupportController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateClientPrincipal()
                }
            }
        };

        var result = controller.Index();

        Assert.IsType<ViewResult>(result);
        Assert.Equal(42L, controller.ViewBag.ClientId);
        Assert.Equal(7L, controller.ViewBag.UserId);
        Assert.Null(controller.ViewBag.GroupChatId);
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
