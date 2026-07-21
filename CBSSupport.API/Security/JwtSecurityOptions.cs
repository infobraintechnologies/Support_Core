using System.Text;

namespace CBSSupport.API.Security;

public sealed class JwtSecurityOptions
{
    public const string SectionName = "Jwt";

    public bool Enabled { get; set; }
    public string? Key { get; set; }
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Key)
            || Encoding.UTF8.GetByteCount(Key) < 32
            || string.IsNullOrWhiteSpace(Issuer)
            || string.IsNullOrWhiteSpace(Audience)
            || AccessTokenLifetime <= TimeSpan.Zero
            || AccessTokenLifetime > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException(
                $"Configuration section '{SectionName}' is invalid for enabled JWT authentication.");
        }
    }
}
