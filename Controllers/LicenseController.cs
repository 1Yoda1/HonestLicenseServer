using HonestLicenseServer.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/license")]
public class LicenseController(HonestDbContext db) : ControllerBase
{
    [HttpGet("current")]
    public async Task<IActionResult> Current()
    {
        var clientId = HttpContext.Items["ClientId"] as int?;
        var deviceId = HttpContext.Items["DeviceId"] as int?;
        if (clientId is null) return Unauthorized();
        if (deviceId is null) return StatusCode(403, new { error = "device_registration_required" });
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId && x.ClientId == clientId);
        if (device?.Status != "Active") return StatusCode(403, new { error = "device_disabled" });

        var license = await db.Licenses.AsNoTracking().Where(x =>
                x.ClientId == clientId && x.DeviceId == deviceId && x.Status == "Active" && x.ValidUntilUtc > DateTime.UtcNow)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync();
        if (license is null) return NotFound(new { error = "active_license_not_found" });
        return Ok(new { license.GrantJson, license.SignatureBase64, license.KeyId,
            license.Revision, license.IssuedAtUtc, license.ValidUntilUtc });
    }
}
