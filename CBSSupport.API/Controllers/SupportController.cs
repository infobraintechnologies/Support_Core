using CBSSupport.API.Security;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Controllers;

[Authorize(Policy = Policies.ClientOnly)]
public sealed class SupportController : Controller
{
    private readonly ILogger<SupportController> _logger;
    private readonly IChatService _chatService;

    public SupportController(ILogger<SupportController> logger, IChatService chatService)
    {
        _logger = logger;
        _chatService = chatService;
    }

    public async Task<IActionResult> Index()
    {
        var clientId = User.GetRequiredClientId();
        var userId = User.GetRequiredUserId();

        ViewBag.UserFullName = User.FindFirst("FullName")?.Value ?? "User";
        ViewBag.ClientId = clientId;
        ViewBag.UserId = userId;
        long groupChatId;

        try
        {
            groupChatId = await _chatService.GetOrCreateGroupChatConversationIdAsync(
                clientId,
                checked((int)userId));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error getting group chat conversation ID for client {ClientId} and user {UserId}",
                clientId,
                userId);
            groupChatId = 1;
        }

        ViewBag.GroupChatId = groupChatId;

        _logger.LogInformation(
            "Support dashboard loaded for user {UserId}, client {ClientId}, group chat {GroupChatId}",
            userId,
            clientId,
            groupChatId);

        return View();
    }
}
