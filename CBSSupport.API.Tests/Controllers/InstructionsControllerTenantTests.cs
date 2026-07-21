using System.Reflection;
using System.Security.Claims;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;
using CBSSupport.Shared.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Tests.Controllers;

public sealed class InstructionsControllerTenantTests
{
    private const long ClientId = 42;
    private const long OtherClientId = 99;

    [Theory]
    [InlineData(nameof(InstructionsController.GetSidebar))]
    [InlineData(nameof(InstructionsController.GetTicketsForClient))]
    [InlineData(nameof(InstructionsController.GetInquiriesForClient))]
    public async Task TenantSelectedEndpoint_ClientRequestsAnotherTenant_ReturnsNotFoundBeforeService(
        string actionName)
    {
        var (controller, service) = CreateClientController();

        var result = actionName switch
        {
            nameof(InstructionsController.GetSidebar) =>
                await controller.GetSidebar(OtherClientId),
            nameof(InstructionsController.GetTicketsForClient) =>
                await controller.GetTicketsForClient(OtherClientId),
            nameof(InstructionsController.GetInquiriesForClient) =>
                await controller.GetInquiriesForClient(OtherClientId),
            _ => throw new ArgumentOutOfRangeException(nameof(actionName))
        };

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task GetMessagesForConversation_ClientRequest_PassesClaimTenantToService()
    {
        var (controller, service) = CreateClientController();
        service.ReturnValues[nameof(IChatService.GetMessagesByConversationIdAsync)] =
            Task.FromResult<IEnumerable<ChatMessage>>(
                [new ChatMessage { Id = 10, ClientId = ClientId }]);

        var result = await controller.GetMessagesForConversation(10);

        Assert.IsType<OkObjectResult>(result);
        var call = Assert.Single(service.Calls);
        Assert.Equal(nameof(IChatService.GetMessagesByConversationIdAsync), call.MethodName);
        Assert.Equal(10L, call.Arguments[0]);
        Assert.Equal(ClientId, call.Arguments[1]);
    }

    [Fact]
    public async Task GetTicketDetails_ClientRequest_PassesClaimTenantToService()
    {
        var (controller, service) = CreateClientController();
        service.ReturnValues[nameof(IChatService.GetTicketDetailsByIdAsync)] =
            Task.FromResult<TicketViewModel?>(new TicketViewModel { Id = 10 });

        var result = await controller.GetTicketDetails(10);

        Assert.IsType<OkObjectResult>(result);
        AssertScopedCall(service, nameof(IChatService.GetTicketDetailsByIdAsync), 10);
    }

    [Fact]
    public async Task GetInquiryDetails_ClientRequest_PassesClaimTenantToService()
    {
        var (controller, service) = CreateClientController();
        service.ReturnValues[nameof(IChatService.GetInquiryDetailsByIdAsync)] =
            Task.FromResult<InquiryViewModel?>(new InquiryViewModel { Id = 10 });

        var result = await controller.GetInquiryDetails(10);

        Assert.IsType<OkObjectResult>(result);
        AssertScopedCall(service, nameof(IChatService.GetInquiryDetailsByIdAsync), 10);
    }

    [Fact]
    public async Task GetUnreadNotifications_ClientRequest_PassesClaimTenantToService()
    {
        var (controller, service) = CreateClientController();
        service.ReturnValues[nameof(IChatService.GetUnreadNotificationsForClientAsync)] =
            Task.FromResult<IEnumerable<object>>(Array.Empty<object>());

        var result = await controller.GetUnreadNotifications();

        Assert.IsType<OkObjectResult>(result);
        var call = Assert.Single(service.Calls);
        Assert.Equal(nameof(IChatService.GetUnreadNotificationsForClientAsync), call.MethodName);
        Assert.Equal(ClientId, Assert.Single(call.Arguments));
    }

    [Theory]
    [InlineData(nameof(InstructionsController.GetTicketsForCurrentClient))]
    [InlineData(nameof(InstructionsController.GetInquiriesForCurrentClient))]
    public async Task CurrentTenantCollectionEndpoint_ClientRequest_PassesClaimTenantToService(
        string actionName)
    {
        var (controller, service) = CreateClientController();
        service.ReturnValues[nameof(IChatService.GetTicketsByClientIdAsync)] =
            Task.FromResult<IEnumerable<TicketViewModel>>(Array.Empty<TicketViewModel>());
        service.ReturnValues[nameof(IChatService.GetInquiriesByClientIdAsync)] =
            Task.FromResult<IEnumerable<InquiryViewModel>>(Array.Empty<InquiryViewModel>());

        var result = actionName switch
        {
            nameof(InstructionsController.GetTicketsForCurrentClient) =>
                await controller.GetTicketsForCurrentClient(),
            nameof(InstructionsController.GetInquiriesForCurrentClient) =>
                await controller.GetInquiriesForCurrentClient(),
            _ => throw new ArgumentOutOfRangeException(nameof(actionName))
        };

        Assert.IsType<OkObjectResult>(result);
        var call = Assert.Single(service.Calls);
        Assert.Equal(ClientId, Assert.Single(call.Arguments));
    }

    [Fact]
    public async Task MarkAllNotificationsSeenByClient_ClientRequest_PassesClaimTenantToService()
    {
        var (controller, service) = CreateClientController();
        service.ReturnValues[nameof(IChatService.MarkAllNotificationsSeenByClientAsync)] =
            Task.FromResult(0);

        var result = await controller.MarkAllNotificationsSeenByClient();

        Assert.IsType<OkObjectResult>(result);
        var call = Assert.Single(service.Calls);
        Assert.Equal(nameof(IChatService.MarkAllNotificationsSeenByClientAsync), call.MethodName);
        Assert.Equal(ClientId, Assert.Single(call.Arguments));
    }

    [Fact]
    public async Task MarkNotificationSeenByClient_ClientRequest_PassesClaimTenantToService()
    {
        var (controller, service) = CreateClientController();
        service.ReturnValues[nameof(IChatService.MarkNotificationSeenByClientAsync)] =
            Task.FromResult(true);

        var result = await controller.MarkNotificationSeenByClient(10);

        Assert.IsType<OkObjectResult>(result);
        AssertScopedCall(service, nameof(IChatService.MarkNotificationSeenByClientAsync), 10);
    }

    private static void AssertScopedCall(
        RecordingChatServiceProxy service,
        string expectedMethodName,
        long expectedResourceId)
    {
        var call = Assert.Single(service.Calls);
        Assert.Equal(expectedMethodName, call.MethodName);
        Assert.Equal(expectedResourceId, call.Arguments[0]);
        Assert.Equal(ClientId, call.Arguments[1]);
    }

    private static (InstructionsController Controller, RecordingChatServiceProxy Service)
        CreateClientController()
    {
        var chatService = DispatchProxy.Create<IChatService, RecordingChatServiceProxy>();
        var recordingService = (RecordingChatServiceProxy)(object)chatService;
        var controller = new InstructionsController(
            chatService,
            new TenantAuthorizationService(),
            null!);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = CreateClientPrincipal()
            }
        };

        return (controller, recordingService);
    }

    private static ClaimsPrincipal CreateClientPrincipal()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "7"),
                new Claim(ClaimTypes.Role, Roles.Client),
                new Claim(CustomClaimTypes.ClientId, ClientId.ToString())
            ],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}

public sealed record RecordedServiceCall(string MethodName, IReadOnlyList<object?> Arguments);

public class RecordingChatServiceProxy : DispatchProxy
{
    public List<RecordedServiceCall> Calls { get; } = [];

    public Dictionary<string, object?> ReturnValues { get; } = new(StringComparer.Ordinal);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        Calls.Add(new RecordedServiceCall(
            targetMethod.Name,
            args is null ? [] : args.ToArray()));

        if (ReturnValues.TryGetValue(targetMethod.Name, out var returnValue))
        {
            return returnValue;
        }

        throw new InvalidOperationException($"No return value configured for {targetMethod.Name}.");
    }
}

public sealed class TenantAuthorizationService : IAuthorizationService
{
    public async Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user,
        object? resource,
        IEnumerable<IAuthorizationRequirement> requirements)
    {
        var requirementList = requirements.ToList();
        var context = new AuthorizationHandlerContext(requirementList, user, resource);
        await new TenantAccessHandler().HandleAsync(context);
        return context.HasSucceeded
            ? AuthorizationResult.Success()
            : AuthorizationResult.Failed();
    }

    public Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user,
        object? resource,
        string policyName) =>
        throw new NotSupportedException("Named policies are enforced by the ASP.NET Core pipeline.");
}
