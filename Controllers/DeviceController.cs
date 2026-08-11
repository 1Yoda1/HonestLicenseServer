using HonestLicenseServer.Authentication;
using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
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
        bool hasBoundActiveDevice = await db.Devices.AsNoTracking().AnyAsync(x =>
            x.ClientId == clientId &&
            x.ExternalDeviceId == externalDeviceId &&
            x.Status == "Active");
        var request = await db.DeviceRegistrationRequests.AsNoTracking()
            .Where(x => x.ClientId == clientId &&
                x.ExternalDeviceId == externalDeviceId &&
                (x.Status != "Approved" || hasBoundActiveDevice))
            .OrderByDescending(x => x.RequestedAtUtc)
            .FirstOrDefaultAsync();

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

        await using var transaction = await DeviceBindingGuard.BeginImmediateWriteAsync(db);
        if (await DeviceBindingGuard.ConflictsWithAnotherClientAsync(
                db, clientId, request.DeviceId))
        {
            return ApiProblems.Create(HttpContext, StatusCodes.Status409Conflict,
                DeviceBindingGuard.ErrorCode,
                "Device is already bound to another client");
        }

        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == request.DeviceId);
        if (device is not null && device.Status != "Deleted")
            return ApiProblems.Create(HttpContext, StatusCodes.Status409Conflict,
                "device_already_registered", "Device is already registered");

        var pending = await db.DeviceRegistrationRequests.SingleOrDefaultAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == request.DeviceId && x.Status == "Pending");
        if (pending is null)
        {
            var existing = device is not null
                ? await db.DeviceRegistrationRequests
                    .Where(x => x.ClientId == clientId && x.ExternalDeviceId == request.DeviceId)
                    .OrderByDescending(x => x.RequestedAtUtc)
                    .FirstOrDefaultAsync()
                : await db.DeviceRegistrationRequests
                    .Where(x => x.ClientId == clientId && x.ExternalDeviceId == request.DeviceId &&
                        x.Status == "Rejected")
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
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                db.Entry(pending).State = EntityState.Detached;
                pending = await db.DeviceRegistrationRequests.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.ClientId == clientId &&
                    x.ExternalDeviceId == request.DeviceId &&
                    x.Status == "Pending");
                if (pending is null) throw;
            }
        }
        await transaction.CommitAsync();
        return Accepted(new DeviceRegistrationResponse(pending.Id, pending.Status, pending.RequestedAtUtc));
    }
}
