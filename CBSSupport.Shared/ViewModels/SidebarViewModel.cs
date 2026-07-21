using CBSSupport.Shared.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CBSSupport.Shared.ViewModels
{
    public class SidebarViewModel
    {
        public List<SidebarChatItem> GroupChats { get; set; } = new List<SidebarChatItem>();
        public List<SidebarChatItem> PrivateChats { get; set; } = new List<SidebarChatItem>();
        public List<SidebarChatItem> InternalChats { get; set; } = new List<SidebarChatItem>();
        public List<SidebarChatItem> TicketChats { get; set; } = new List<SidebarChatItem>();
        public List<SidebarChatItem> InquiryChats { get; set; } = new List<SidebarChatItem>();
    }
}
