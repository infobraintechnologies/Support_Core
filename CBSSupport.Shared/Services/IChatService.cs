using CBSSupport.Shared.Models;
using CBSSupport.Shared.ViewModels;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CBSSupport.Shared.Services
{
    public interface IChatService
    {
        Task<IEnumerable<ChatMessage>> GetInstructionTicketsForUserAsync(long clientAuthUserId);

        Task<IEnumerable<ChatMessage>> GetConversationsByInstTypeAsync(short instTypeId, long? clientId = null);

        Task<ChatMessage?> CreateInstructionTicketAsync(
            ChatMessage newTicket,
            CancellationToken cancellationToken = default);

        Task<ChatMessage> GetInstructionByIdAsync(long instructionId);

        Task<IEnumerable<ChatMessage>> GetMessagesByConversationIdAsync(long conversationId, long? clientId = null);

        Task<SidebarViewModel> GetSidebarForUserAsync(long clientAuthUserId, long clientId);

        Task<IEnumerable<TicketViewModel>> GetTicketsByClientIdAsync(long clientId);

        Task<IEnumerable<InquiryViewModel>> GetInquiriesByClientIdAsync(long clientId);

        Task<IEnumerable<ClientUser>> GetAllClientsAsync();

        Task<IEnumerable<TicketViewModel>> GetAllTicketsAsync();

        Task<IEnumerable<InquiryViewModel>> GetAllInquiriesAsync();


        Task<IEnumerable<TicketViewModel>> GetSolvedTicketsAsync();

        Task<IEnumerable<TicketViewModel>> GetUnsolvedTicketsAsync();

        Task<IEnumerable<InquiryViewModel>> GetSolvedInquiriesAsync();

        Task<IEnumerable<InquiryViewModel>> GetUnsolvedInquiriesAsync();

        Task<DashboardStatsViewModel> GetDashboardStatsAsync();

        Task<bool> UpdateInstructionAsync(ChatMessage instruction);

        Task<long> GetOrCreateGroupChatConversationIdAsync(long clientId, int loggedInUserId);

        Task<ChatMessage> CreateGroupChatMessageAsync(ChatMessage newMessage);

        Task<bool> UpdateTicketStatusAsync(long ticketId, bool isCompleted, long? completedByUserId = null);

        Task<bool> UpdateInquiryStatusAsync(long inquiryId, bool isCompleted, long? completedByUserId = null);

        Task<TicketViewModel?> GetTicketDetailsByIdAsync(long ticketId, long? clientId = null);

        Task<InquiryViewModel?> GetInquiryDetailsByIdAsync(long inquiryId, long? clientId = null);

        Task<IEnumerable<object>> GetUnreadNotificationsForAdminAsync();

        Task<bool> MarkNotificationSeenByAdminAsync(long instructionId);

        Task<int> MarkAllNotificationsSeenByAdminAsync();

        Task<bool> MarkNotificationSeenByClientAsync(long instructionId, long clientId);

        Task<IEnumerable<object>> GetUnreadNotificationsForClientAsync(long clientId);

        Task<int> MarkAllNotificationsSeenByClientAsync(long clientId);
    }
}
