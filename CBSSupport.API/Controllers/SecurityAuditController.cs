using CBSSupport.API.Security;
using CBSSupport.Shared.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CBSSupport.API.Controllers;

[ApiController]
[Route("api/v1/security-audit")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class SecurityAuditController(ISecurityAuditReader auditReader) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SecurityAuditRecord>>> List(
        [FromQuery] long? tenantId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (tenantId is <= 0 || limit is < 1 or > 100)
        {
            return ValidationProblem("Audit query parameters are invalid.");
        }

        return Ok(await auditReader.ListAsync(tenantId, from, limit, cancellationToken));
    }
}
