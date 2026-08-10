using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/version")]
public class VersionController(HonestDbContext db) : ControllerBase
{
    [HttpGet("current/{application}")]
    [AllowAnonymous]
    [ProducesResponseType<VersionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VersionResponse>> Current(string application)
    {
        var version = await db.AppVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Application == application);
        return version is null
            ? ApiProblems.Create(HttpContext, StatusCodes.Status404NotFound,
                "application_not_found", "Application was not found")
            : Ok(new VersionResponse(version.Application, version.CurrentVersion, version.ImportedAtUtc));
    }
}
