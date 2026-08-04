using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CBSSupport.Shared.Models
{
    public class Instruction
    {
        public long Id { get; set; }
        public DateTime Datetime { get; set; }
        public int? UserId { get; set; }
        public long? IntroId { get; set; }
        public long? IdenId { get; set; }
        public long? AccountId { get; set; }
        public long? DemandId { get; set; }
        public short InstCategoryId { get; set; }
        public short InstTypeId { get; set; }
        public string InstructionText { get; set; } 
        public DateTime? AlertDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public long? InstructionId { get; set; }
        public int? AssignedTo { get; set; }
        public bool? Completed { get; set; }
        public int? CompletedBy { get; set; }
        public DateTime? CompletedOn { get; set; }
        public string CompletedRemarks { get; set; }
        public bool Status { get; set; }
        public string Remarks { get; set; }
        public int? InsertUser { get; set; }
        public DateTime InsertDate { get; set; }
        public int? EditUser { get; set; }
        public DateTime? EditDate { get; set; }
        public long? ClientId { get; set; }
        public long? ClientUserId { get; set; }
        public int? ClientAuthUserId { get; set; }
        public long? ServiceId { get; set; }

        public string AttachmentId { get; set; }
        public string InstChannel { get; set; }
        public string IpAddress { get; set; }
    }

}
