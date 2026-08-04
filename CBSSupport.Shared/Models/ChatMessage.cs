
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CBSSupport.Shared.Models
{
    public class ChatMessage
    {
        public long Id { get; set; }
        public DateTime DateTime { get; set; }
        public short InstTypeId { get; set; }
        public bool Status { get; set; }
        public DateTime InsertDate { get; set; }
        public string? InstChannel { get; set; }
        public string? IpAddress { get; set; }

        [NotMapped]
        public string? SenderName { get; set; }

        [Required(ErrorMessage = "Instruction text cannot be empty.")]
        [StringLength(4000, MinimumLength = 1, ErrorMessage = "Instruction must be between 1 and 4000 characters.")]
        public string? Instruction { get; set; }
        public int? UserId { get; set; }
        public long? ClientId { get; set; }
        public long? ClientUserId { get; set; }
        public int? ClientAuthUserId { get; set; }

        public int? InsertUser { get; set; }
        public long? IntroId { get; set; }
        public long? IdenId { get; set; }
        public long? AccountId { get; set; }
        public long? DemandId { get; set; }
        public short InstCategoryId { get; set; } 
        public DateTime? AlertDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public long? InstructionId { get; set; }
        public int? AssignedTo { get; set; }
        public bool? Completed { get; set; }
        public int? CompletedBy { get; set; }
        public DateTime? CompletedOn { get; set; }
        public string? CompletedRemarks { get; set; }

        public string? Priority { get; set; }
        public string? Remarks { get; set; }
        public DateTime? EditDate { get; set; }
        public long? EditUser { get; set; }
        public long? ServiceId { get; set; }
        public string? AttachmentId { get; set; }
        public string? GeoLocation { get; set; }
        public Guid? ClientMessageId { get; set; }
        public long? ConversationSequence { get; set; }
    }
}
