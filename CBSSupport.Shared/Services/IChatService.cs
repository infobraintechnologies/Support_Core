using CBSSupport.Shared.Models;
using CBSSupport.Shared.ViewModels;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CBSSupport.Shared.Services
{
    public interface IChatService
    {
        Task<IEnumerable<ChatMessage>> GetInstructionTicketsForUserAsync(int clientAuthUserId);

        Task<IEnumerable<ChatMessage>> GetConversationsByInstTypeAsync(short instTypeId, long? clientId = null);

        Task<ChatMessage?> CreateInstructionTicketAsync(
            ChatMessage newTicket,
            CancellationToken cancellationToken = default);

        Task<ChatMessage> GetInstructionByIdAsync(long instructionId);

        Task<IEnumerable<ChatMessage>> GetMessagesByConversationIdAsync(long conversationId, long? clientId = null);

        Task<SidebarViewModel> GetSidebarForUserAsync(long clientAuthUserId, long clientId);

        Task<IEnumerable<TicketViewModel>> GetTicketsByClientIdAsync(long clientId);

        Task<CBSSupport.Shared.Contracts.CasePage<TicketViewModel>> ListTicketsAsync(
            CBSSupport.Shared.Contracts.CaseListCriteria criteria,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<TicketViewModel>> GetTicketsByClientIdAsync(
            long clientId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetTicketsByClientIdAsync(clientId);
        }

        Task<IEnumerable<InquiryViewModel>> GetInquiriesByClientIdAsync(long clientId);

        Task<CBSSupport.Shared.Contracts.CasePage<InquiryViewModel>> ListInquiriesAsync(
            CBSSupport.Shared.Contracts.CaseListCriteria criteria,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<InquiryViewModel>> GetInquiriesByClientIdAsync(
            long clientId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetInquiriesByClientIdAsync(clientId);
        }

        Task<IEnumerable<ClientUser>> GetAllClientsAsync();

        Task<IEnumerable<TicketViewModel>> GetAllTicketsAsync();

        Task<IEnumerable<TicketViewModel>> GetAllTicketsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetAllTicketsAsync();
        }

        Task<IEnumerable<InquiryViewModel>> GetAllInquiriesAsync();

        Task<IEnumerable<InquiryViewModel>> GetAllInquiriesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetAllInquiriesAsync();
        }


        Task<IEnumerable<TicketViewModel>> GetSolvedTicketsAsync();

        Task<IEnumerable<TicketViewModel>> GetUnsolvedTicketsAsync();

        Task<IEnumerable<InquiryViewModel>> GetSolvedInquiriesAsync();

        Task<IEnumerable<InquiryViewModel>> GetUnsolvedInquiriesAsync();

        Task<DashboardStatsViewModel> GetDashboardStatsAsync();

        Task<long?> GetOrCreateGroupChatConversationIdAsync(long clientId, int clientAuthUserId);

        Task<ChatMessage?> CreateGroupChatMessageAsync(ChatMessage newMessage);

        Task<TicketViewModel?> GetTicketDetailsByIdAsync(long ticketId, long? clientId = null);

        Task<TicketViewModel?> GetTicketDetailsByIdAsync(
            long ticketId,
            long? clientId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetTicketDetailsByIdAsync(ticketId, clientId);
        }

        Task<InquiryViewModel?> GetInquiryDetailsByIdAsync(long inquiryId, long? clientId = null);

        Task<InquiryViewModel?> GetInquiryDetailsByIdAsync(
            long inquiryId,
            long? clientId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetInquiryDetailsByIdAsync(inquiryId, clientId);
        }

        Task<IEnumerable<object>> GetUnreadNotificationsForAdminAsync();

        Task<bool> MarkNotificationSeenByAdminAsync(long instructionId);

        Task<int> MarkAllNotificationsSeenByAdminAsync();

        Task<bool> MarkNotificationSeenByClientAsync(long instructionId, long clientId);

        Task<IEnumerable<object>> GetUnreadNotificationsForClientAsync(long clientId);

        Task<int> MarkAllNotificationsSeenByClientAsync(long clientId);
    }
}
