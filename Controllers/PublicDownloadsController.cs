using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/downloads/honestflow")]
public sealed class PublicDownloadsController(
    HonestDbContext db,
    IReleaseObjectStorage releaseStorage,
    IOptions<DiagnosticStorageOptions> storageOptions) : ControllerBase
{
    private const string InstallerComponent = "HonestFlowInstaller";

    [HttpGet("setup")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DownloadSetup(CancellationToken cancellationToken)
    {
        string? version = await db.AppVersions.AsNoTracking()
            .Where(x => x.Application == InstallerComponent)
            .Select(x => x.CurrentVersion)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(version))
            return Missing("honestflow_installer_not_published", "HonestFlow installer is not published");

        var asset = (await db.ComponentAssets.AsNoTracking()
                .Where(x => x.Component == InstallerComponent && x.Version == version &&
                    (x.Architecture == "x64" || x.Architecture == "any"))
                .ToListAsync(cancellationToken))
            .OrderBy(x => x.Architecture == "x64" ? 0 : 1)
            .FirstOrDefault();
        if (asset is null || string.IsNullOrWhiteSpace(asset.ObjectKey) ||
            string.IsNullOrWhiteSpace(asset.ReleaseKeyId) ||
            string.IsNullOrWhiteSpace(asset.ManifestBase64) ||
            string.IsNullOrWhiteSpace(asset.ReleaseSignatureBase64) ||
            asset.SizeBytes is not > 0 || asset.Sha256?.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit))
            return Missing("honestflow_installer_unavailable", "HonestFlow installer is unavailable");

        if (!releaseStorage.IsConfigured)
            return ApiProblems.Create(HttpContext, StatusCodes.Status503ServiceUnavailable,
                "release_storage_unavailable", "Release storage is unavailable");

        DateTime expires = DateTime.UtcNow.AddMinutes(
            Math.Clamp(storageOptions.Value.DownloadUrlMinutes, 2, 60));
        Response.Headers.CacheControl = "no-store";
        return Redirect(releaseStorage.CreateDownloadUrl(asset.ObjectKey, asset.FileName, expires));
    }

    private ActionResult Missing(string code, string detail) =>
        ApiProblems.Create(HttpContext, StatusCodes.Status404NotFound, code, detail);
}
