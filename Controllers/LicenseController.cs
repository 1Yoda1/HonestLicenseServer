using HonestLicenseServer.Authentication;
using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

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
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<ActionResult<LicenseResponse>> Current()
    {
        var clientId = User.ClientId();
        var deviceId = User.DeviceId()!.Value;
        var license = await db.Licenses.AsNoTracking().Where(x =>
                x.ClientId == clientId && x.DeviceId == deviceId &&
                x.SignatureScope == "PersonalGrant" && x.SignatureVerifiedAtUtc != null)
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

        var etagHash = Convert.ToHexString(SHA256.HashData(license.GrantBytes))[..16];
        var etag = $"\"license-{license.Revision}-{etagHash}\"";
        Response.Headers.ETag = etag;
        if (Request.Headers.IfNoneMatch.Any(value =>
                string.Equals(value, etag, StringComparison.Ordinal) || value == "*"))
            return StatusCode(StatusCodes.Status304NotModified);

        return Ok(new LicenseResponse(Convert.ToBase64String(license.GrantBytes),
            license.SignatureBase64, license.KeyId, license.Revision,
            license.IssuedAtUtc, license.ValidUntilUtc));
    }
}
