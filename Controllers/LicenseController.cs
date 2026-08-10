using System.Text;
using HonestLicenseServer.Authentication;
using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/license")]
public class LicenseController(HonestDbContext db) : ControllerBase
{
    [HttpGet("current")]
    [Authorize(Policy = OpaqueBearerDefaults.ActiveDevicePolicy)]
    [ProducesResponseType<LicenseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    public async Task<ActionResult<LicenseResponse>> Current()
    {
        var clientId = User.ClientId();
        var deviceId = User.DeviceId()!.Value;
        var license = await db.Licenses.AsNoTracking().Where(x =>
                x.ClientId == clientId && x.DeviceId == deviceId)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync();
        if (license is null)
            return ApiProblems.Create(HttpContext, StatusCodes.Status404NotFound,
                "license_not_found", "License was not found");
        if (license.Status == "Revoked")
            return ApiProblems.Create(HttpContext, StatusCodes.Status410Gone,
                "license_revoked", "License has been revoked");
        if (license.ValidUntilUtc <= DateTime.UtcNow || license.Status == "Expired")
            return ApiProblems.Create(HttpContext, StatusCodes.Status410Gone,
                "license_expired", "License has expired");
        if (license.Status != "Active")
            return ApiProblems.Create(HttpContext, StatusCodes.Status404NotFound,
                "license_not_found", "An active license was not found");

        return Ok(new LicenseResponse(Convert.ToBase64String(Encoding.UTF8.GetBytes(license.GrantJson)),
            license.SignatureBase64, license.KeyId, license.Revision,
            license.IssuedAtUtc, license.ValidUntilUtc));
    }
}
