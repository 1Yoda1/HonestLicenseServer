using HonestLicenseServer.Authentication;
using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/device")]
public class DeviceController(HonestDbContext db) : ControllerBase
{
    [HttpPost("request")]
    [Authorize(Policy = OpaqueBearerDefaults.ActiveClientPolicy)]
    [ProducesResponseType<DeviceRegistrationResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceRegistrationResponse>> RequestRegistration(DeviceRegistrationRequestDto request)
    {
        var clientId = User.ClientId();
        var externalDeviceId = User.ExternalDeviceId();
        if (request.DeviceId != externalDeviceId)
            return ApiProblems.Create(HttpContext, StatusCodes.Status400BadRequest,
                "device_id_does_not_match_token", "Device ID does not match the authenticated session");
        if (await db.Devices.AnyAsync(x => x.ClientId == clientId && x.ExternalDeviceId == request.DeviceId))
            return ApiProblems.Create(HttpContext, StatusCodes.Status409Conflict,
                "device_already_registered", "Device is already registered");

        var pending = await db.DeviceRegistrationRequests.SingleOrDefaultAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == request.DeviceId && x.Status == "Pending");
        if (pending is null)
        {
            pending = new DeviceRegistrationRequest { ClientId = clientId,
                ExternalDeviceId = request.DeviceId, RequestedName = request.Name,
                Status = "Pending", RequestedAtUtc = DateTime.UtcNow };
            db.DeviceRegistrationRequests.Add(pending);
            await db.SaveChangesAsync();
        }
        return Accepted(new DeviceRegistrationResponse(pending.Id, pending.Status, pending.RequestedAtUtc));
    }
}
