using CBSSupport.Shared.Data;

namespace CBSSupport.API.Security;

public sealed class SecurityAuditMiddleware(
    RequestDelegate next,
    ISecurityAuditWriter auditWriter,
    ILogger<SecurityAuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (context.Response.StatusCode is not (StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden))
        {
            return;
        }

        var audit = SecurityAuditContext.ForHttpRequest(
            context,
            "UnauthorizedAccessAttempt",
            SecurityAuditOutcomes.Denied,
            details: new Dictionary<string, string?>
            {
                ["statusCode"] = context.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        try
        {
            await auditWriter.AppendAsync(audit, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Security audit append failed for an unauthorized access attempt");
        }
    }
}
