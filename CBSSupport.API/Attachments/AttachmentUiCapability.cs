using CBSSupport.Shared.Services;

namespace CBSSupport.API.Attachments;

public sealed class AttachmentUiCapability(
    AttachmentOptions options,
    IFileScanner? scanner,
    TimeProvider timeProvider)
{
    public bool CanCreateUploadIntents
    {
        get
        {
            if (!options.Enabled)
            {
                return false;
            }
            if (options.SecurityMode == AttachmentSecurityMode.StructuralValidationOnly)
            {
                return true;
            }

            var health = scanner?.Health;
            return health is { Healthy: true }
                && timeProvider.GetUtcNow() - health.CheckedAt
                    <= TimeSpan.FromSeconds(options.Scanning.HealthCheckSeconds * 2);
        }
    }
}
