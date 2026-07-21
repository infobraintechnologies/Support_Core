using System;

namespace CBSSupport.Shared.ViewModels
{
    public class TicketViewModel
    {
        public long Id { get; set; }
        public string Subject { get; set; }
        public DateTime Date { get; set; }
        public string CreatedBy { get; set; }
        public string ResolvedBy { get; set; }
        public string Status { get; set; }
        public string Priority { get; set; }
        public string ClientName { get; set; }

        public long ClientId { get; set; }

        public string Description { get; set; }  
        public string Remarks { get; set; }     
        public DateTime? ExpiryDate { get; set; }
    }
}