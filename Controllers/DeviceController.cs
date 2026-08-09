using HonestLicenseServer.Data;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/device")]
public class DeviceController(HonestDbContext db) : ControllerBase
{
    [HttpPost("request")]
    public async Task<IActionResult> RequestRegistration(DeviceRequest request)
    {
        var clientId = HttpContext.Items["ClientId"] as int?;
        var externalDeviceId = HttpContext.Items["ExternalDeviceId"] as string;
        if (clientId is null || externalDeviceId is null) return Unauthorized();
        if (request.DeviceId != externalDeviceId)
            return BadRequest(new { error = "device_id_does_not_match_token" });
        if (await db.Devices.AnyAsync(x => x.ClientId == clientId && x.ExternalDeviceId == request.DeviceId))
            return Conflict(new { error = "device_already_registered" });

        var pending = await db.DeviceRegistrationRequests.SingleOrDefaultAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == request.DeviceId && x.Status == "Pending");
        if (pending is null)
        {
            pending = new DeviceRegistrationRequest { ClientId = clientId.Value,
                ExternalDeviceId = request.DeviceId, RequestedName = request.Name,
                Status = "Pending", RequestedAtUtc = DateTime.UtcNow };
            db.DeviceRegistrationRequests.Add(pending);
            await db.SaveChangesAsync();
        }
        return Accepted(new { pending.Id, pending.Status, pending.RequestedAtUtc });
    }
}

public record DeviceRequest(string DeviceId, string Name);
