using System.Security.Claims;
using System.Reflection;
using CBSSupport.API.Controllers;
using CBSSupport.API.Security;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Models;
using CBSSupport.Shared.Services;
using CBSSupport.Shared.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Tests.Controllers;

public sealed class TicketsInquiriesApiV1ControllerTests
{
    private const long ClientId = 42;
    private const long AdminUserId = 106;

    // ---- Authorization attributes ----

    [Theory]
    [InlineData(nameof(TicketsController.Create), Policies.ClientOnly)]
    [InlineData(nameof(TicketsController.List), Policies.ClientOnly)]
    [InlineData(nameof(TicketsController.GetDetail), Policies.AdminOrClient)]
    public void TicketsController_ActionPolicy_IsCorrect(string actionName, string expectedPolicy)
    {
        var method = typeof(TicketsController).GetMethod(actionName)!;
        Assert.Equal(expectedPolicy, AuthorizePolicy(method));
    }

    [Fact]
    public void AdminTicketsController_TypePolicy_IsAdminOnly()
    {
        Assert.Equal(Policies.AdminOnly, AuthorizePolicy(typeof(AdminTicketsController)));
    }

    [Theory]
    [InlineData(nameof(InquiriesController.Create), Policies.ClientOnly)]
    [InlineData(nameof(InquiriesController.List), Policies.ClientOnly)]
    [InlineData(nameof(InquiriesController.GetDetail), Policies.AdminOrClient)]
    public void InquiriesController_ActionPolicy_IsCorrect(string actionName, string expectedPolicy)
    {
        var method = typeof(InquiriesController).GetMethod(actionName)!;
        Assert.Equal(expectedPolicy, AuthorizePolicy(method));
    }

    [Fact]
    public void AdminInquiriesController_TypePolicy_IsAdminOnly()
    {
        Assert.Equal(Policies.AdminOnly, AuthorizePolicy(typeof(AdminInquiriesController)));
    }

    // ---- Ticket creation ----

    [Fact]
    public async Task CreateTicket_Valid_ReturnsCreatedWithClaimDerivedTenant()
    {
        var conversation = new RecordingCaseConversationService
        {
            CreateResult = new(
                ConversationCommandStatus.Created,
                new ChatMessage
                {
                    Id = 234,
                    InstructionId = 234,
                    DateTime = DateTime.UtcNow,
                    InstTypeId = ConversationTypes.MigrationTicket,
                    ClientId = ClientId,
                    SenderName = "Client User",
                    Completed = false
                })
        };
        var chat = new RecordingChatService();
        var controller = CreateTicketsController(conversation, chat, principal: CreateClientPrincipal());

        var result = await controller.Create(
            new CreateTicketRequest("Posting mismatch", "Observed after closing batch 18.", CaseTypes.Migration, CasePriorities.High),
            CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var response = Assert.IsType<TicketResponse>(created.Value);
        Assert.Equal(234, response.Id);
        Assert.Equal(CaseTypes.Migration, response.Type);
        Assert.Equal("Open", response.Status);
        Assert.Equal(ClientId, response.ClientId);
        Assert.Equal(ClientId, conversation.LastActor!.ClientId);
        Assert.False(conversation.LastActor!.IsAdmin);
        Assert.Equal(ConversationTypes.MigrationTicket, conversation.LastTypeCode);
        Assert.Equal(InstructionCategories.Ticket, conversation.LastCategory);
        Assert.Equal("Posting mismatch", conversation.LastSubject);
    }

    [Fact]
    public async Task CreateTicket_InvalidType_ReturnsValidationProblem()
    {
        var controller = CreateTicketsController(
            new RecordingCaseConversationService(),
            new RecordingChatService(),
            CreateClientPrincipal());

        var result = await controller.Create(
            new CreateTicketRequest("Subj", "Desc", "not-a-type", null),
            CancellationToken.None);

        var problem = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        var details = Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task CreateTicket_InvalidPriority_ReturnsValidationProblem()
    {
        var controller = CreateTicketsController(
            new RecordingCaseConversationService(),
            new RecordingChatService(),
            CreateClientPrincipal());

        var result = await controller.Create(
            new CreateTicketRequest("Subj", "Desc", CaseTypes.Migration, "Immediate"),
            CancellationToken.None);

        var problem = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        var details = Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task CreateTicket_Unavailable_ReturnsNotFound()
    {
        var conversation = new RecordingCaseConversationService
        {
            CreateResult = new(ConversationCommandStatus.Unavailable, ErrorCode: "client_identity_unavailable")
        };
        var controller = CreateTicketsController(conversation, new RecordingChatService(), CreateClientPrincipal());

        var result = await controller.Create(
            new CreateTicketRequest("Subj", "Desc", CaseTypes.Migration, null),
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- Ticket detail tenant isolation ----

    [Fact]
    public async Task GetTicketDetail_ClientRequest_PassesClaimTenantAndScopesResult()
    {
        var chat = new RecordingChatService
        {
            Detail = (id, clientId) =>
                id == 10 && clientId == ClientId
                    ? new TicketViewModel { Id = 10, InstTypeId = ConversationTypes.MigrationTicket, ClientId = ClientId, Status = "Open", Subject = "S", Date = DateTime.UtcNow }
                    : null
        };
        var controller = CreateTicketsController(new RecordingCaseConversationService(), chat, CreateClientPrincipal());

        // Record belongs to the caller's tenant, so it is returned.
        var owned = await controller.GetDetail(10, CancellationToken.None);
        Assert.IsType<OkObjectResult>(owned.Result);

        // A different resource ID resolves to null under the claim tenant, so it must disappear as 404.
        var missing = await controller.GetDetail(99, CancellationToken.None);
        Assert.IsType<NotFoundResult>(missing.Result);

        var calls = chat.Calls.Where(c => c.Method == "GetTicketDetailsByIdAsync").ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal(ClientId, call.GetArg<long?>(1)));
    }

    [Fact]
    public async Task GetTicketDetail_AdminRequest_PassesNullScope()
    {
        var chat = new RecordingChatService
        {
            Detail = (id, _) => new TicketViewModel { Id = 10, InstTypeId = ConversationTypes.MigrationTicket, Status = "Open", Date = DateTime.UtcNow }
        };
        var controller = CreateTicketsController(new RecordingCaseConversationService(), chat, CreateAdminPrincipal());

        var result = await controller.GetDetail(10, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var call = Assert.Single(chat.Calls, c => c.Method == "GetTicketDetailsByIdAsync");
        Assert.Null(call.GetArg<long?>(1));
    }

    [Fact]
    public async Task GetTicketDetail_UnknownTicket_ReturnsNotFound()
    {
        var controller = CreateTicketsController(
            new RecordingCaseConversationService(),
            new RecordingChatService(),
            CreateClientPrincipal());

        var result = await controller.GetDetail(10, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- Ticket list ----

    [Fact]
    public async Task ListTickets_Client_ScopesToClaimTenant()
    {
        var chat = new RecordingChatService
        {
            ClientTickets =
            [
                new TicketViewModel { Id = 1, InstTypeId = ConversationTypes.MigrationTicket, Status = "Open", ClientId = ClientId, Date = DateTime.UtcNow }
            ]
        };
        var controller = CreateTicketsController(new RecordingCaseConversationService(), chat, CreateClientPrincipal());

        var result = await controller.List(new CaseListQuery(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<CasePage<TicketResponse>>(ok.Value);
        var item = Assert.Single(page.Items);
        Assert.Equal(CaseTypes.Migration, item.Type);
        Assert.Equal(CaseListQuery.DefaultPageSize, page.PageSize);
        Assert.Equal(ClientId, chat.LastTicketCriteria!.ClientId);
    }

    [Fact]
    public async Task ListTickets_MaximumPageSizeAndUnknownSort_AreRejected()
    {
        var chat = new RecordingChatService();
        var controller = CreateTicketsController(new RecordingCaseConversationService(), chat, CreateClientPrincipal());

        var overLimit = await controller.List(
            new CaseListQuery { PageSize = CaseListQuery.MaximumPageSize + 1 },
            CancellationToken.None);
        var invalidSort = await controller.List(
            new CaseListQuery { Sort = "insertUser" },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(overLimit.Result);
        Assert.IsType<BadRequestObjectResult>(invalidSort.Result);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task ListTickets_CombinedFilters_AreAllowlistedAndClaimScoped()
    {
        var chat = new RecordingChatService
        {
            TicketPage = new CasePage<TicketViewModel>([], 10, null)
        };
        var controller = CreateTicketsController(new RecordingCaseConversationService(), chat, CreateClientPrincipal());

        var result = await controller.List(
            new CaseListQuery
            {
                PageSize = 10,
                Status = "resolved",
                Type = CaseTypes.Migration,
                Priority = "high",
                Sort = CasePagination.TypeSort,
                Direction = CasePagination.Ascending
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var criteria = chat.LastTicketCriteria!;
        Assert.Equal(ClientId, criteria.ClientId);
        Assert.True(criteria.IsCompleted);
        Assert.Equal(ConversationTypes.MigrationTicket, criteria.TypeCode);
        Assert.Equal(CasePriorities.High, criteria.Priority);
        Assert.Equal(CasePagination.TypeSort, criteria.Sort);
        Assert.Equal(CasePagination.Ascending, criteria.Direction);
    }

    [Fact]
    public async Task ListTickets_ClientCannotOverrideClaimTenant()
    {
        var chat = new RecordingChatService();
        var controller = CreateTicketsController(new RecordingCaseConversationService(), chat, CreateClientPrincipal());

        var result = await controller.List(
            new CaseListQuery { ClientId = ClientId + 1 },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(chat.LastTicketCriteria);
    }

    [Fact]
    public async Task ListTickets_CursorPagesWithEqualSortValues_RemainDisjoint()
    {
        var firstCursor = CasePagination.EncodeCursor(new CaseListCursor(
            CasePagination.StatusSort,
            CasePagination.Ascending,
            20,
            null,
            0,
            null,
            null));
        var chat = new RecordingChatService
        {
            TicketPage = new CasePage<TicketViewModel>(
                [new TicketViewModel { Id = 20, Status = "Open", Date = DateTime.UtcNow }],
                1,
                firstCursor)
        };
        var controller = CreateTicketsController(new RecordingCaseConversationService(), chat, CreateClientPrincipal());

        var first = await controller.List(
            new CaseListQuery { PageSize = 1, Sort = CasePagination.StatusSort, Direction = CasePagination.Ascending },
            CancellationToken.None);
        chat.TicketPage = new CasePage<TicketViewModel>(
            [new TicketViewModel { Id = 21, Status = "Open", Date = DateTime.UtcNow }],
            1,
            null);
        var firstPage = Assert.IsType<OkObjectResult>(first.Result).Value as CasePage<TicketResponse>;
        var second = await controller.List(
            new CaseListQuery
            {
                PageSize = 1,
                Sort = CasePagination.StatusSort,
                Direction = CasePagination.Ascending,
                Cursor = firstPage!.NextCursor
            },
            CancellationToken.None);

        var secondPage = Assert.IsType<OkObjectResult>(second.Result).Value as CasePage<TicketResponse>;
        Assert.DoesNotContain(secondPage!.Items[0].Id, firstPage.Items.Select(item => item.Id));
        Assert.Equal(20, chat.LastTicketCriteria!.Cursor!.Id);
    }

    [Fact]
    public async Task ListTickets_CancelledRequest_StopsBeforeQuery()
    {
        var controller = CreateTicketsController(
            new RecordingCaseConversationService(),
            new RecordingChatService(),
            CreateClientPrincipal());
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.List(new CaseListQuery(), source.Token));
    }

    // ---- Admin status updates ----

    [Fact]
    public async Task UpdateTicketStatus_ValidTransition_ReturnsUpdatedTicket()
    {
        var chat = new RecordingChatService();
        var ticket = new TicketViewModel
        {
            Id = 10,
            InstTypeId = ConversationTypes.MigrationTicket,
            Status = "Resolved",
            Date = DateTime.UtcNow,
            CreatedBy = "Alice"
        };
        chat.Detail = (_, _) => ticket;
        var tickets = new RecordingTicketService { MutationResult = new(CaseMutationStatus.Updated, 2, ClientId) };
        var controller = new AdminTicketsController(chat, tickets)
        {
            ControllerContext = ControllerContextFor(CreateAdminPrincipal())
        };

        var result = await controller.UpdateStatus(10, new UpdateCaseStatusRequest("Resolved", 1), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<TicketResponse>(ok.Value);
        Assert.Equal("Resolved", response.Status);
        Assert.Single(tickets.StatusCommands);
    }

    [Theory]
    [InlineData(null, StatusCodes.Status400BadRequest)]
    [InlineData("Bogus", StatusCodes.Status400BadRequest)]
    public async Task UpdateTicketStatus_InvalidStatus_ReturnsBadRequest(string? status, int expectedStatus)
    {
        var chat = new RecordingChatService
        {
            Detail = (_, _) => new TicketViewModel { Id = 10, Status = "Open", Date = DateTime.UtcNow }
        };
        var tickets = new RecordingTicketService();
        var controller = new AdminTicketsController(chat, tickets) { ControllerContext = ControllerContextFor(CreateAdminPrincipal()) };

        var result = await controller.UpdateStatus(10, new UpdateCaseStatusRequest(status, 1), CancellationToken.None);

        var problem = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        var details = Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.Equal(expectedStatus, details.Status);
        Assert.Empty(tickets.StatusCommands);
    }

    [Fact]
    public async Task UpdateTicketStatus_NoOpTransition_ReturnsConflictWithoutWriting()
    {
        var chat = new RecordingChatService();
        var tickets = new RecordingTicketService { MutationResult = new(CaseMutationStatus.InvalidState, 1, ClientId) };
        var controller = new AdminTicketsController(chat, tickets) { ControllerContext = ControllerContextFor(CreateAdminPrincipal()) };

        var result = await controller.UpdateStatus(10, new UpdateCaseStatusRequest("Resolved", 1), CancellationToken.None);

        var conflict = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Single(tickets.StatusCommands);
    }

    [Fact]
    public async Task UpdateTicketStatus_MissingTicket_ReturnsNotFound()
    {
        var controller = new AdminTicketsController(
            new RecordingChatService(),
            new RecordingTicketService { MutationResult = new(CaseMutationStatus.NotFound) })
        {
            ControllerContext = ControllerContextFor(CreateAdminPrincipal())
        };

        var result = await controller.UpdateStatus(10, new UpdateCaseStatusRequest("Resolved", 1), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateInquiryStatus_UsesDedicatedInquiryServiceWithClaimDerivedActor()
    {
        var inquiries = new RecordingInquiryService
        {
            MutationResult = new(CaseMutationStatus.Updated, 4, ClientId)
        };
        var chat = new RecordingChatService
        {
            InquiryDetail = (_, _) => new InquiryViewModel
            {
                Id = 14,
                InstTypeId = ConversationTypes.AccountsInquiry,
                Outcome = "Completed",
                Date = DateTime.UtcNow
            }
        };
        var controller = new AdminInquiriesController(chat, inquiries)
        {
            ControllerContext = ControllerContextFor(CreateAdminPrincipal())
        };

        var result = await controller.UpdateStatus(
            14,
            new UpdateCaseStatusRequest("Completed", 3),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var command = Assert.Single(inquiries.StatusCommands);
        Assert.Equal(14, command.CaseId);
        Assert.True(command.IsCompleted);
        Assert.Equal(AdminUserId, command.ActorUserId);
        Assert.Equal(3, command.ExpectedVersion);
    }

    // ---- Inquiry creation ----

    [Fact]
    public async Task CreateInquiry_Valid_ReturnsCreatedWithClaimDerivedTenant()
    {
        var conversation = new RecordingCaseConversationService
        {
            CreateResult = new(
                ConversationCommandStatus.Created,
                new ChatMessage { Id = 7, InstructionId = 7, DateTime = DateTime.UtcNow, InstTypeId = ConversationTypes.AccountsInquiry, ClientId = ClientId, SenderName = "Client User" })
        };
        var controller = CreateInquiriesController(conversation, new RecordingChatService(), CreateClientPrincipal());

        var result = await controller.Create(
            new CreateInquiryRequest("Account statement format", "Clarification requested.", CaseTypes.Accounts, CasePriorities.Normal),
            CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var response = Assert.IsType<InquiryResponse>(created.Value);
        Assert.Equal(CaseTypes.Accounts, response.Type);
        Assert.Equal("Pending", response.Status);
        Assert.Equal(ConversationTypes.AccountsInquiry, conversation.LastTypeCode);
        Assert.Equal(InstructionCategories.Inquiry, conversation.LastCategory);
    }

    [Fact]
    public async Task CreateInquiry_InvalidType_ReturnsValidationProblem()
    {
        var controller = CreateInquiriesController(
            new RecordingCaseConversationService(),
            new RecordingChatService(),
            CreateClientPrincipal());

        var result = await controller.Create(
            new CreateInquiryRequest("Topic", "Desc", "nope", null),
            CancellationToken.None);

        var problem = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        var details = Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task GetInquiryDetail_ClientRequest_PassesClaimTenant()
    {
        var chat = new RecordingChatService
        {
            InquiryDetail = (id, clientId) =>
                id == 7 && clientId == ClientId
                    ? new InquiryViewModel { Id = 7, Topic = "Accounts", InstTypeId = ConversationTypes.AccountsInquiry, Date = DateTime.UtcNow }
                    : null
        };
        var controller = CreateInquiriesController(new RecordingCaseConversationService(), chat, CreateClientPrincipal());

        var result = await controller.GetDetail(7, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var call = Assert.Single(chat.Calls, c => c.Method == "GetInquiryDetailsByIdAsync");
        Assert.Equal(ClientId, call.GetArg<long?>(1));
    }

    [Fact]
    public async Task ListInquiries_DefaultPageAndFilters_AreClaimScoped()
    {
        var chat = new RecordingChatService
        {
            InquiryPage = new CasePage<InquiryViewModel>([], CaseListQuery.DefaultPageSize, null)
        };
        var controller = CreateInquiriesController(new RecordingCaseConversationService(), chat, CreateClientPrincipal());

        var result = await controller.List(
            new CaseListQuery { Status = "Completed", Type = CaseTypes.Accounts, Priority = "Normal" },
            CancellationToken.None);

        var page = Assert.IsType<CasePage<InquiryResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(CaseListQuery.DefaultPageSize, page.PageSize);
        Assert.Equal(ClientId, chat.LastInquiryCriteria!.ClientId);
        Assert.True(chat.LastInquiryCriteria.IsCompleted);
        Assert.Equal(ConversationTypes.AccountsInquiry, chat.LastInquiryCriteria.TypeCode);
        Assert.Equal(CasePriorities.Normal, chat.LastInquiryCriteria.Priority);
    }

    [Fact]
    public async Task GetInquiryDetail_CrossTenantResource_IsNotFound()
    {
        var chat = new RecordingChatService
        {
            InquiryDetail = (_, clientId) =>
                clientId == ClientId
                    ? null
                    : new InquiryViewModel { Id = 8, InstTypeId = ConversationTypes.AccountsInquiry, Date = DateTime.UtcNow }
        };
        var controller = CreateInquiriesController(new RecordingCaseConversationService(), chat, CreateClientPrincipal());

        var result = await controller.GetDetail(8, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        var call = Assert.Single(chat.Calls, c => c.Method == "GetInquiryDetailsByIdAsync");
        Assert.Equal(ClientId, call.GetArg<long?>(1));
    }

    // ---- Helpers ----

    private static TicketsController CreateTicketsController(
        IConversationService conversation,
        IChatService chat,
        ClaimsPrincipal principal) =>
        new(conversation, chat) { ControllerContext = ControllerContextFor(principal) };

    private static InquiriesController CreateInquiriesController(
        IConversationService conversation,
        IChatService chat,
        ClaimsPrincipal principal) =>
        new(conversation, chat) { ControllerContext = ControllerContextFor(principal) };

    private static ControllerContext ControllerContextFor(ClaimsPrincipal principal) =>
        new()
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

    private static ClaimsPrincipal CreateClientPrincipal() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "7"),
                new Claim(ClaimTypes.Name, "Client User"),
                new Claim(ClaimTypes.Role, Roles.Client),
                new Claim(CustomClaimTypes.ClientId, ClientId.ToString())
            ],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role));

    private static ClaimsPrincipal CreateAdminPrincipal() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, AdminUserId.ToString()),
                new Claim(ClaimTypes.Name, "Administrator"),
                new Claim(ClaimTypes.Role, Roles.Admin)
            ],
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role));

    private static string? AuthorizePolicy(MethodInfo method) =>
        method.GetCustomAttributes<AuthorizeAttribute>().FirstOrDefault()?.Policy;

    private static string? AuthorizePolicy(Type type) =>
        type.GetCustomAttributes<AuthorizeAttribute>().FirstOrDefault()?.Policy;

    private sealed record RecordedCall(string Method, object?[] Args)
    {
        public T GetArg<T>(int index) => (T)Args[index]!;
    }

    private sealed class RecordingChatService : IChatService
    {
        public List<RecordedCall> Calls { get; } = [];
        public Func<long, long?, TicketViewModel?> Detail { get; set; } = (_, _) => null;
        public Func<long, long?, InquiryViewModel?> InquiryDetail { get; set; } = (_, _) => null;
        public IEnumerable<TicketViewModel> ClientTickets { get; set; } = [];
        public IEnumerable<InquiryViewModel> ClientInquiries { get; set; } = [];
        public CasePage<TicketViewModel>? TicketPage { get; set; }
        public CasePage<InquiryViewModel>? InquiryPage { get; set; }
        public CaseListCriteria? LastTicketCriteria { get; private set; }
        public CaseListCriteria? LastInquiryCriteria { get; private set; }
        public IEnumerable<TicketViewModel> AllTickets { get; set; } = [];
        public IEnumerable<InquiryViewModel> AllInquiries { get; set; } = [];

        public Task<TicketViewModel?> GetTicketDetailsByIdAsync(long ticketId, long? clientId = null)
        {
            Calls.Add(new RecordedCall(nameof(GetTicketDetailsByIdAsync), [ticketId, clientId]));
            return Task.FromResult(Detail(ticketId, clientId));
        }

        public Task<InquiryViewModel?> GetInquiryDetailsByIdAsync(long inquiryId, long? clientId = null)
        {
            Calls.Add(new RecordedCall(nameof(GetInquiryDetailsByIdAsync), [inquiryId, clientId]));
            return Task.FromResult(InquiryDetail(inquiryId, clientId));
        }

        public Task<IEnumerable<TicketViewModel>> GetTicketsByClientIdAsync(long clientId)
        {
            Calls.Add(new RecordedCall(nameof(GetTicketsByClientIdAsync), [clientId]));
            return Task.FromResult(ClientTickets);
        }

        public Task<CasePage<TicketViewModel>> ListTicketsAsync(
            CaseListCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastTicketCriteria = criteria;
            Calls.Add(new RecordedCall(nameof(ListTicketsAsync), [criteria]));
            return Task.FromResult(TicketPage ?? new CasePage<TicketViewModel>(
                ClientTickets.ToArray(),
                criteria.PageSize,
                null));
        }

        public Task<IEnumerable<InquiryViewModel>> GetInquiriesByClientIdAsync(long clientId)
        {
            Calls.Add(new RecordedCall(nameof(GetInquiriesByClientIdAsync), [clientId]));
            return Task.FromResult(ClientInquiries);
        }

        public Task<CasePage<InquiryViewModel>> ListInquiriesAsync(
            CaseListCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastInquiryCriteria = criteria;
            Calls.Add(new RecordedCall(nameof(ListInquiriesAsync), [criteria]));
            return Task.FromResult(InquiryPage ?? new CasePage<InquiryViewModel>(
                ClientInquiries.ToArray(),
                criteria.PageSize,
                null));
        }

        public Task<IEnumerable<TicketViewModel>> GetAllTicketsAsync()
        {
            Calls.Add(new RecordedCall(nameof(GetAllTicketsAsync), []));
            return Task.FromResult(AllTickets);
        }

        public Task<IEnumerable<InquiryViewModel>> GetAllInquiriesAsync()
        {
            Calls.Add(new RecordedCall(nameof(GetAllInquiriesAsync), []));
            return Task.FromResult(AllInquiries);
        }

        public Task<IEnumerable<ChatMessage>> GetInstructionTicketsForUserAsync(int clientAuthUserId) => throw new NotSupportedException();
        public Task<IEnumerable<ChatMessage>> GetConversationsByInstTypeAsync(short instTypeId, long? clientId = null) => throw new NotSupportedException();
        public Task<ChatMessage?> CreateInstructionTicketAsync(ChatMessage newTicket, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ChatMessage> GetInstructionByIdAsync(long instructionId) => throw new NotSupportedException();
        public Task<IEnumerable<ChatMessage>> GetMessagesByConversationIdAsync(long conversationId, long? clientId = null) => throw new NotSupportedException();
        public Task<SidebarViewModel> GetSidebarForUserAsync(long clientAuthUserId, long clientId) => throw new NotSupportedException();
        public Task<IEnumerable<ClientUser>> GetAllClientsAsync() => throw new NotSupportedException();
        public Task<IEnumerable<TicketViewModel>> GetSolvedTicketsAsync() => throw new NotSupportedException();
        public Task<IEnumerable<TicketViewModel>> GetUnsolvedTicketsAsync() => throw new NotSupportedException();
        public Task<IEnumerable<InquiryViewModel>> GetSolvedInquiriesAsync() => throw new NotSupportedException();
        public Task<IEnumerable<InquiryViewModel>> GetUnsolvedInquiriesAsync() => throw new NotSupportedException();
        public Task<DashboardStatsViewModel> GetDashboardStatsAsync() => throw new NotSupportedException();
        public Task<long?> GetOrCreateGroupChatConversationIdAsync(long clientId, int clientAuthUserId) => throw new NotSupportedException();
        public Task<ChatMessage?> CreateGroupChatMessageAsync(ChatMessage newMessage) => throw new NotSupportedException();
        public Task<IEnumerable<object>> GetUnreadNotificationsForAdminAsync() => throw new NotSupportedException();
        public Task<bool> MarkNotificationSeenByAdminAsync(long instructionId) => throw new NotSupportedException();
        public Task<int> MarkAllNotificationsSeenByAdminAsync() => throw new NotSupportedException();
        public Task<bool> MarkNotificationSeenByClientAsync(long instructionId, long clientId) => throw new NotSupportedException();
        public Task<IEnumerable<object>> GetUnreadNotificationsForClientAsync(long clientId) => throw new NotSupportedException();
        public Task<int> MarkAllNotificationsSeenByClientAsync(long clientId) => throw new NotSupportedException();
    }

    private sealed class RecordingTicketService : ITicketService
    {
        public List<CaseStatusUpdateCommand> StatusCommands { get; } = [];
        public CaseMutationResult MutationResult { get; set; } = new(CaseMutationStatus.NotFound);

        public Task<CaseMutationResult> UpdateStatusAsync(CaseStatusUpdateCommand command, CancellationToken cancellationToken = default)
        {
            StatusCommands.Add(command);
            return Task.FromResult(MutationResult);
        }

        public Task<CaseMutationResult> UpdateAsync(TicketUpdateCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(MutationResult);
    }

    private sealed class RecordingInquiryService : IInquiryService
    {
        public List<CaseStatusUpdateCommand> StatusCommands { get; } = [];
        public CaseMutationResult MutationResult { get; set; } = new(CaseMutationStatus.NotFound);

        public Task<CaseMutationResult> UpdateStatusAsync(CaseStatusUpdateCommand command, CancellationToken cancellationToken = default)
        {
            StatusCommands.Add(command);
            return Task.FromResult(MutationResult);
        }
    }


    private sealed class RecordingCaseConversationService : IConversationService
    {
        public ConversationCommandResult<ChatMessage> CreateResult { get; set; } = new(ConversationCommandStatus.Invalid);
        public ConversationActor? LastActor { get; private set; }
        public short? LastTypeCode { get; private set; }
        public short? LastCategory { get; private set; }
        public string? LastSubject { get; private set; }
        public string? LastPriority { get; private set; }

        public Task<ConversationCommandResult<ChatMessage>> CreateCaseAsync(
            ConversationActor actor,
            short instructionTypeId,
            short instructionCategoryId,
            string text,
            string? priority,
            string? remarks,
            DateTime? expiryDate,
            string? ipAddress,
            CancellationToken cancellationToken = default,
            string? subject = null)
        {
            LastActor = actor;
            LastTypeCode = instructionTypeId;
            LastCategory = instructionCategoryId;
            LastSubject = subject;
            LastPriority = priority;
            return Task.FromResult(CreateResult);
        }

        public Task<ConversationAccess?> GetAccessAsync(
            long conversationId,
            ConversationActor actor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationAccess?>(null);

        public Task<ConversationMessage?> CreateMessageAsync(
            long conversationId,
            ConversationActor actor,
            string text,
            string? ipAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationMessage?>(null);
    }
}
