namespace CBSSupport.Shared.Models
{
    public class ClientUser
    {
        public long Id { get; set; }
        public long ClientId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;

        public string PasswordHash { get; set; } = string.Empty;
        public string PasswordSalt { get; set; } = string.Empty;

        public bool Status { get; set; }
        public DateTimeOffset? DeactiveDate { get; set; }
    }
}
