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
    [HttpGet("registration/current")]
    [Authorize(Policy = OpaqueBearerDefaults.ActiveClientPolicy)]
    [ProducesResponseType<RegistrationStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegistrationStatusResponse>> CurrentRegistration()
    {
        var clientId = User.ClientId();
        var externalDeviceId = User.ExternalDeviceId();
        var request = await db.DeviceRegistrationRequests.AsNoTracking()
            .Where(x => x.ClientId == clientId && x.ExternalDeviceId == externalDeviceId)
            .OrderByDescending(x => x.RequestedAtUtc)
            .FirstOrDefaultAsync();

        if (request is not null && request.Status != "Pending" && User.DeviceId() is null)
        {
            var deletedDeviceExists = await db.Devices.AsNoTracking().AnyAsync(x =>
                x.ClientId == clientId && x.ExternalDeviceId == externalDeviceId && x.Status == "Deleted");
            var sessionCreatedAtUtc = await db.RefreshTokens.AsNoTracking()
                .Where(x => x.Id == User.SessionId())
                .Select(x => (DateTime?)x.CreatedAtUtc)
                .SingleOrDefaultAsync();
            if (deletedDeviceExists && sessionCreatedAtUtc is not null &&
                (request.ResolvedAtUtc is null || request.ResolvedAtUtc <= sessionCreatedAtUtc))
            {
                request = null;
            }
        }

        if (request is null && User.DeviceId() is int deviceId)
        {
            var registeredAtUtc = await db.Devices.AsNoTracking()
                .Where(x => x.Id == deviceId)
                .Select(x => x.RegisteredAtUtc)
                .SingleAsync();
            return Ok(new RegistrationStatusResponse(externalDeviceId, "Approved",
                registeredAtUtc, registeredAtUtc, null));
        }
        if (request is null)
            return ApiProblems.Create(HttpContext, StatusCodes.Status404NotFound,
                "device_request_not_found", "Device registration request was not found");

        return Ok(new RegistrationStatusResponse(request.ExternalDeviceId, request.Status,
            request.RequestedAtUtc, request.ResolvedAtUtc, request.Comment));
    }

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
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == request.DeviceId);
        if (device is not null && device.Status != "Deleted")
            return ApiProblems.Create(HttpContext, StatusCodes.Status409Conflict,
                "device_already_registered", "Device is already registered");

        var pending = await db.DeviceRegistrationRequests.SingleOrDefaultAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == request.DeviceId && x.Status == "Pending");
        if (pending is null)
        {
            var existing = await db.DeviceRegistrationRequests
                .Where(x => x.ClientId == clientId && x.ExternalDeviceId == request.DeviceId)
                .OrderByDescending(x => x.RequestedAtUtc)
                .FirstOrDefaultAsync();
            if (existing is not null)
            {
                existing.RequestedName = request.Name;
                existing.RequestedAddress = request.Address.Trim();
                existing.RequestedHonestFlowVersion = string.IsNullOrWhiteSpace(request.HonestFlowVersion)
                    ? null : request.HonestFlowVersion.Trim();
                existing.Status = "Pending";
                existing.RequestedAtUtc = DateTime.UtcNow;
                existing.ResolvedAtUtc = null;
                existing.Comment = null;
                pending = existing;
            }
            else
            {
                pending = new DeviceRegistrationRequest { ClientId = clientId,
                    ExternalDeviceId = request.DeviceId, RequestedName = request.Name,
                    RequestedAddress = request.Address.Trim(),
                    RequestedHonestFlowVersion = string.IsNullOrWhiteSpace(request.HonestFlowVersion)
                        ? null : request.HonestFlowVersion.Trim(),
                    Status = "Pending", RequestedAtUtc = DateTime.UtcNow };
                db.DeviceRegistrationRequests.Add(pending);
            }
            await db.SaveChangesAsync();
        }
        return Accepted(new DeviceRegistrationResponse(pending.Id, pending.Status, pending.RequestedAtUtc));
    }
}
