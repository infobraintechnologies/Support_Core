namespace CBSSupport.Shared.Helpers;

/// <summary>
/// Configuration required to verify passwords stored by the CBS company systems.
/// The pepper is deployment secret material and must never be committed.
/// </summary>
public sealed class PasswordHashOptions
{
    public const string SectionName = "Security:PasswordHashing";

    public string Pepper { get; set; } = string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Pepper))
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:Pepper' is required for company password verification.");
        }
    }
}
