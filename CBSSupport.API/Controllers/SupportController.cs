using CBSSupport.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Controllers;

[Authorize(Policy = Policies.ClientOnly)]
public sealed class SupportController : Controller
{
    private readonly ILogger<SupportController> _logger;

    public SupportController(ILogger<SupportController> logger)
    {
        _logger = logger;
    }

    public IActionResult Index()
    {
        var clientId = User.GetRequiredClientId();
        var userId = User.GetRequiredUserId();

        ViewBag.UserFullName = User.FindFirst("FullName")?.Value ?? "User";
        ViewBag.ClientId = clientId;
        ViewBag.UserId = userId;
        // Messaging V2 creates/loads the tenant group through an authenticated POST.
        // A GET must never mutate state or substitute another conversation identifier.
        ViewBag.GroupChatId = null;

        _logger.LogInformation(
            "Support dashboard loaded for user {UserId}, client {ClientId}",
            userId,
            clientId);

        return View();
    }
}
