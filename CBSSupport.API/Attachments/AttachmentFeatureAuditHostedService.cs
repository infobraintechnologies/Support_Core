using CBSSupport.API.Configuration;
using CBSSupport.Shared.Data;
using CBSSupport.Shared.Services;

namespace CBSSupport.API.Attachments;

public sealed class AttachmentFeatureAuditHostedService(
    AttachmentOptions options,
    ISecurityAuditWriter auditWriter,
    ILogger<AttachmentFeatureAuditHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            await auditWriter.AppendAsync(
                new SecurityAuditEvent(
                    null,
                    SecurityAuditActorKinds.System,
                    null,
                    "Feature",
                    "attachments",
                    "AttachmentFeatureEnabled",
                    SecurityAuditOutcomes.Success,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    new Dictionary<string, string?> { ["source"] = "startup" },
                    new Dictionary<string, string?>
                    {
                        ["securityMode"] = options.SecurityMode.ToString()
                    }),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                exception,
                "Attachment enablement could not be durably recorded; refusing to continue startup");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
