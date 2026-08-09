using HonestLicenseServer.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/version")]
public class VersionController(HonestDbContext db) : ControllerBase
{
    [HttpGet("current/{application}")]
    public async Task<IActionResult> Current(string application)
    {
        var version = await db.AppVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Application == application);
        return version is null ? NotFound(new { error = "application_not_found" })
            : Ok(new { version.Application, version.CurrentVersion, version.ImportedAtUtc });
    }
}
