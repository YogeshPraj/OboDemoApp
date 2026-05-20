using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CMSPDemo.PartnerAPI.Controllers;

[ApiController]
public sealed class HealthController : ControllerBase
{
    [HttpGet("/api/health")]
    [AllowAnonymous]
    public IActionResult Health()
        => Ok(new { service = "PartnerAPI", ok = true, ts = DateTimeOffset.UtcNow });
}
