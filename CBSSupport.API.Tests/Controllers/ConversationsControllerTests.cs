using System.Security.Claims;
using CBSSupport.API.Configuration;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CBSSupport.API.Tests.Controllers;

public sealed class ConversationsControllerTests
{
    [Fact]
    public async Task SendMessage_UsesClaimDerivedActorAndReturnsCreatedCanonicalMessage()
    {
        var service = new RecordingConversationService
        {
            Access = new ConversationAccess(
                25,
                42,
                ConversationTypes.SupportGroup,
                ConversationTypes.SupportGroup,
                ConversationStates.Active,
                null,
                null,
                1)
        };
        var messageId = Guid.NewGuid();
        service.SendResult = new(
            ConversationCommandStatus.Created,
            new ConversationMessage(
                501,
                25,
                "hello",
                DateTime.UtcNow,
                new ConversationSender(7, "Client User", ConversationParticipantKinds.Client),
                ClientMessageId: messageId,
                Sequence: 9,
                Attachments: []));
        var controller = CreateController(service, privateEnabled: false);

        var result = await controller.SendMessage(
            25,
            new SendMessageV2Request(messageId, "hello"),
            CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.Same(service.SendResult.Value, created.Value);
        Assert.NotNull(service.SentActor);
        Assert.Equal(7, service.SentActor.UserId);
        Assert.Equal(42, service.SentActor.ClientId);
        Assert.False(service.SentActor.IsAdmin);
        Assert.Equal(messageId, service.SentClientMessageId);
        Assert.Empty(service.SentAttachmentIds);
    }

    [Fact]
    public async Task List_WhenGroupAndPrivateAreDisabled_StillReturnsCases()
    {
        var service = new RecordingConversationService
        {
            ListedConversations =
            [
                new ConversationSummary(
                    25,
                    42,
                    ConversationKinds.Ticket,
                    ConversationStates.Active,
                    null,
                    null,
                    null,
                    null,
                    4,
                    1,
                    3,
                    DateTime.UtcNow,
                    1),
                new ConversationSummary(
                    26,
                    42,
                    ConversationKinds.Group,
                    ConversationStates.Active,
                    null,
                    null,
                    null,
                    null,
                    2,
                    0,
                    2,
                    DateTime.UtcNow,
                    1)
            ]
        };
        var controller = CreateController(
            service,
            privateEnabled: false,
            groupEnabled: false);

        var result = await controller.List(cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<ConversationPage<ConversationSummary>>(ok.Value);
        var item = Assert.Single(page.Items);
        Assert.Equal(ConversationKinds.Ticket, item.Kind);
    }

    [Fact]
    public async Task SendMessage_WhenAccessIsUnavailable_ReturnsNotFoundWithoutWriting()
    {
        var service = new RecordingConversationService();
        var controller = CreateController(service, privateEnabled: true);

        var result = await controller.SendMessage(
            25,
            new SendMessageV2Request(Guid.NewGuid(), "hello"),
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Null(service.SentActor);
    }

    [Fact]
    public async Task CreatePrivate_WhenFeatureIsDisabled_ReturnsNotFoundWithoutRepositoryCall()
    {
        var service = new RecordingConversationService();
        var controller = CreateController(service, privateEnabled: false);

        var result = await controller.GetOrCreatePrivate(
            new CreatePrivateConversationRequest(11),
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.False(service.PrivateCreateCalled);
    }

    [Fact]
    public async Task CreateGroup_WhenFeatureIsDisabled_ReturnsNotFoundWithoutServiceCall()
    {
        var service = new RecordingConversationService();
        var controller = CreateController(
            service,
            privateEnabled: true,
            groupEnabled: false);

        var result = await controller.GetOrCreateGroup(CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.False(service.GroupCreateCalled);
    }

    [Fact]
    public async Task AvailableAdmins_AllowsAuthenticatedAdminActorForTransferPicker()
    {
        var service = new RecordingConversationService
        {
            AvailableAdmins =
            [
                new ConversationDirectoryUser(106, "Administrator")
            ]
        };
        var controller = CreateController(service, privateEnabled: true);
        controller.ControllerContext.HttpContext.User = CreateAdminPrincipal();

        var result = await controller.GetAvailableAdmins(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(service.AvailableAdmins, ok.Value);
        Assert.NotNull(service.DirectoryActor);
        Assert.True(service.DirectoryActor.IsAdmin);
        Assert.Null(service.DirectoryActor.ClientId);
    }

    [Fact]
    public async Task SendMessage_IdempotencyConflictPreservesStableProblemCode()
    {
        var service = new RecordingConversationService
        {
            Access = new ConversationAccess(
                25,
                42,
                ConversationTypes.SupportGroup,
                ConversationTypes.SupportGroup,
                ConversationStates.Active,
                null,
                null,
                1),
            SendResult = new(
                ConversationCommandStatus.Conflict,
                ErrorCode: "idempotency_conflict")
        };
        var controller = CreateController(service, privateEnabled: false);

        var result = await controller.SendMessage(
            25,
            new SendMessageV2Request(Guid.NewGuid(), "hello"),
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.Equal("idempotency_conflict", details.Extensions["code"]);
    }

    [Theory]
    [InlineData(ConversationCommandStatus.Success, StatusCodes.Status200OK)]
    [InlineData(ConversationCommandStatus.Unavailable, StatusCodes.Status404NotFound)]
    [InlineData(ConversationCommandStatus.Conflict, StatusCodes.Status409Conflict)]
    public async Task Transfer_MapsLifecycleStatus(
        ConversationCommandStatus status,
        int expectedStatusCode)
    {
        var summary = CreateSummary(version: 2);
        var service = new RecordingConversationService
        {
            TransferResult = new(
                status,
                status == ConversationCommandStatus.Success ? summary : null,
                status == ConversationCommandStatus.Conflict
                    ? "conversation_version_conflict"
                    : null)
        };
        var controller = CreateController(service, privateEnabled: true);
        controller.ControllerContext.HttpContext.User = CreateAdminPrincipal();

        var result = await controller.Transfer(
            25,
            new TransferConversationRequest(106, 1, "handoff"),
            CancellationToken.None);

        Assert.Equal(expectedStatusCode, GetStatusCode(result.Result));
        Assert.Equal(1, service.TransferCallCount);
        Assert.NotNull(service.TransferActor);
        Assert.Equal(106, service.TransferTargetAdminUserId);
        Assert.Equal(1, service.TransferExpectedVersion);

        if (status == ConversationCommandStatus.Conflict)
        {
            var problem = Assert.IsType<ObjectResult>(result.Result);
            var details = Assert.IsType<ProblemDetails>(problem.Value);
            Assert.Equal("conversation_version_conflict", details.Extensions["code"]);
        }
    }

    [Fact]
    public async Task Transfer_WhenPrivateFeatureIsDisabled_ReturnsNotFoundWithoutServiceCall()
    {
        var service = new RecordingConversationService();
        var controller = CreateController(service, privateEnabled: false);
        controller.ControllerContext.HttpContext.User = CreateAdminPrincipal();

        var result = await controller.Transfer(
            25,
            new TransferConversationRequest(106, 1, null),
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(0, service.TransferCallCount);
    }

    [Theory]
    [InlineData(ConversationCommandStatus.Success, StatusCodes.Status200OK)]
    [InlineData(ConversationCommandStatus.Unavailable, StatusCodes.Status404NotFound)]
    [InlineData(ConversationCommandStatus.Conflict, StatusCodes.Status409Conflict)]
    public async Task Archive_MapsLifecycleStatus(
        ConversationCommandStatus status,
        int expectedStatusCode)
    {
        var summary = CreateSummary(ConversationStates.Archived, version: 2);
        var service = new RecordingConversationService
        {
            ArchiveResult = new(
                status,
                status == ConversationCommandStatus.Success ? summary : null,
                status == ConversationCommandStatus.Conflict
                    ? "conversation_version_conflict"
                    : null)
        };
        var controller = CreateController(service, privateEnabled: true);

        var result = await controller.Archive(
            25,
            new ArchiveConversationRequest(1),
            CancellationToken.None);

        Assert.Equal(expectedStatusCode, GetStatusCode(result.Result));
        Assert.Equal(1, service.ArchiveCallCount);
        Assert.NotNull(service.ArchiveActor);
        Assert.Equal(1, service.ArchiveExpectedVersion);

        if (status == ConversationCommandStatus.Conflict)
        {
            var problem = Assert.IsType<ObjectResult>(result.Result);
            var details = Assert.IsType<ProblemDetails>(problem.Value);
            Assert.Equal("conversation_version_conflict", details.Extensions["code"]);
        }
    }

    [Fact]
    public async Task Archive_WhenPrivateFeatureIsDisabled_ReturnsNotFoundWithoutServiceCall()
    {
        var service = new RecordingConversationService();
        var controller = CreateController(service, privateEnabled: false);

        var result = await controller.Archive(
            25,
            new ArchiveConversationRequest(1),
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(0, service.ArchiveCallCount);
    }

    private static ConversationsController CreateController(
        RecordingConversationService service,
        bool privateEnabled,
        bool groupEnabled = true)
    {
        var controller = new ConversationsController(
            service,
            Options.Create(new MessagingFeatureOptions
            {
                GroupEnabled = groupEnabled,
                PrivateEnabled = privateEnabled
            }))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateClientPrincipal()
                }
            }
        };
        return controller;
    }

    private static int? GetStatusCode(ActionResult? result) =>
        result switch
        {
            ObjectResult objectResult => objectResult.StatusCode,
            StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
            _ => null
        };

    private static ConversationSummary CreateSummary(
        string state = ConversationStates.Active,
        long version = 1) =>
        new(
            25,
            42,
            "Private",
            state,
            7,
            "Client User",
            106,
            "Administrator",
            4,
            4,
            0,
            DateTime.UtcNow,
            version);

    private static ClaimsPrincipal CreateClientPrincipal() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "7"),
                new Claim(ClaimTypes.Name, "Client User"),
                new Claim(ClaimTypes.Role, Roles.Client),
                new Claim(CustomClaimTypes.ClientId, "42")
            ],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role));

    private static ClaimsPrincipal CreateAdminPrincipal() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "106"),
                new Claim(ClaimTypes.Name, "Administrator"),
                new Claim(ClaimTypes.Role, Roles.Admin)
            ],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role));

    private sealed class RecordingConversationService : IConversationService
    {
        public ConversationAccess? Access { get; init; }
        public ConversationCommandResult<ConversationMessage> SendResult { get; set; } =
            new(ConversationCommandStatus.Invalid);
        public ConversationActor? SentActor { get; private set; }
        public Guid? SentClientMessageId { get; private set; }
        public IReadOnlyList<Guid> SentAttachmentIds { get; private set; } = [];
        public IReadOnlyList<ConversationSummary> ListedConversations { get; init; } = [];
        public bool PrivateCreateCalled { get; private set; }
        public bool GroupCreateCalled { get; private set; }
        public IReadOnlyList<ConversationDirectoryUser> AvailableAdmins { get; init; } = [];
        public ConversationActor? DirectoryActor { get; private set; }
        public ConversationCommandResult<ConversationSummary> TransferResult { get; init; } =
            new(ConversationCommandStatus.Invalid);
        public ConversationCommandResult<ConversationSummary> ArchiveResult { get; init; } =
            new(ConversationCommandStatus.Invalid);
        public int TransferCallCount { get; private set; }
        public ConversationActor? TransferActor { get; private set; }
        public long? TransferTargetAdminUserId { get; private set; }
        public long? TransferExpectedVersion { get; private set; }
        public int ArchiveCallCount { get; private set; }
        public ConversationActor? ArchiveActor { get; private set; }
        public long? ArchiveExpectedVersion { get; private set; }

        public Task<ConversationAccess?> GetAccessAsync(
            long conversationId,
            ConversationActor actor,
            CancellationToken cancellationToken = default) => Task.FromResult(Access);

        public Task<ConversationMessage?> CreateMessageAsync(
            long conversationId,
            ConversationActor actor,
            string text,
            string? ipAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationMessage?>(null);

        public Task<IReadOnlyList<ConversationSummary>> ListAsync(
            ConversationActor actor,
            int limit = 50,
            long? beforeConversationId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ListedConversations);

        public Task<ConversationCommandResult<ConversationMessage>> SendMessageAsync(
            long conversationId,
            ConversationActor actor,
            Guid clientMessageId,
            string? text,
            IReadOnlyList<Guid> attachmentIds,
            string? ipAddress,
            CancellationToken cancellationToken = default)
        {
            SentActor = actor;
            SentClientMessageId = clientMessageId;
            SentAttachmentIds = attachmentIds.ToArray();
            return Task.FromResult(SendResult);
        }

        public Task<ConversationCommandResult<ConversationSummary>> GetOrCreatePrivateAsync(
            ConversationActor actor,
            long counterpartyUserId,
            CancellationToken cancellationToken = default)
        {
            PrivateCreateCalled = true;
            return Task.FromResult(new ConversationCommandResult<ConversationSummary>(
                ConversationCommandStatus.Invalid));
        }

        public Task<ConversationCommandResult<ConversationSummary>> GetOrCreateGroupAsync(
            ConversationActor actor,
            long? adminSelectedClientId,
            CancellationToken cancellationToken = default)
        {
            GroupCreateCalled = true;
            return Task.FromResult(new ConversationCommandResult<ConversationSummary>(
                ConversationCommandStatus.Invalid));
        }

        public Task<ConversationCommandResult<ConversationSummary>> TransferAsync(
            long conversationId,
            ConversationActor actor,
            long targetAdminUserId,
            long expectedVersion,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            TransferCallCount++;
            TransferActor = actor;
            TransferTargetAdminUserId = targetAdminUserId;
            TransferExpectedVersion = expectedVersion;
            return Task.FromResult(TransferResult);
        }

        public Task<ConversationCommandResult<ConversationSummary>> ArchiveAsync(
            long conversationId,
            ConversationActor actor,
            long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            ArchiveCallCount++;
            ArchiveActor = actor;
            ArchiveExpectedVersion = expectedVersion;
            return Task.FromResult(ArchiveResult);
        }

        public Task<IReadOnlyList<ConversationDirectoryUser>> GetAvailableAdminsAsync(
            ConversationActor actor,
            CancellationToken cancellationToken = default)
        {
            DirectoryActor = actor;
            return Task.FromResult(AvailableAdmins);
        }
    }
}
