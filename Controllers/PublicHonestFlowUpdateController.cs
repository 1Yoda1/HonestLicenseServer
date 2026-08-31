using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/updates/honestflow")]
public sealed class PublicHonestFlowUpdateController(
    HonestDbContext db,
    IYandexPublicDownloadResolver yandex,
    ILogger<PublicHonestFlowUpdateController> logger) : ControllerBase
{
    private const string Application = "HonestFlow";

    [HttpGet("current")]
    [AllowAnonymous]
    [ProducesResponseType<HonestFlowUpdateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HonestFlowUpdateResponse>> Current(
        [FromQuery] string? architecture = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedArchitecture = AssetsController.NormalizeArchitecture(architecture);
        var version = await db.AppVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Application == Application, cancellationToken);
        if (version is null)
            return Missing("honestflow_update_not_published", "HonestFlow update is not published");

        var asset = await FindPublishedAssetAsync(version.CurrentVersion, normalizedArchitecture, cancellationToken);
        if (asset is null || !HasTrustedMetadata(asset))
            return Missing("honestflow_update_metadata_incomplete", "HonestFlow update metadata is incomplete");
        if (!HasDownloadSource(asset))
            return Missing("honestflow_update_asset_unavailable", "HonestFlow update asset is unavailable");

        return Ok(new HonestFlowUpdateResponse(
            Application, version.CurrentVersion, asset.FileName, asset.Architecture,
            asset.SizeBytes!.Value, asset.Sha256!,
            $"{Request.Scheme}://{Request.Host}/api/updates/honestflow/{Uri.EscapeDataString(version.CurrentVersion)}/download?architecture={Uri.EscapeDataString(normalizedArchitecture)}"));
    }

    [HttpGet("{version}/download")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Download(string version, [FromQuery] string? architecture = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedArchitecture = AssetsController.NormalizeArchitecture(architecture);
        bool isPublished = await db.AppVersions.AsNoTracking()
            .AnyAsync(x => x.Application == Application && x.CurrentVersion == version, cancellationToken);
        if (!isPublished)
            return Missing("honestflow_update_not_published", "HonestFlow update is not published");

        var asset = await FindPublishedAssetAsync(version, normalizedArchitecture, cancellationToken);
        if (asset is null || !HasTrustedMetadata(asset) || !HasDownloadSource(asset))
            return Missing("honestflow_update_asset_unavailable", "HonestFlow update asset is unavailable");
        if (!string.IsNullOrWhiteSpace(asset.DownloadUrl)) return Redirect(asset.DownloadUrl);

        try
        {
            string? href = await yandex.ResolveAsync(asset.YandexPublicKey!, asset.YandexPath!, cancellationToken);
            return href is null
                ? ApiProblems.Create(HttpContext, StatusCodes.Status502BadGateway,
                    "yandex_download_link_unavailable", "Download link is unavailable")
                : Redirect(href);
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "Could not resolve public HonestFlow update {Version}", version);
            return ApiProblems.Create(HttpContext, StatusCodes.Status502BadGateway,
                "yandex_download_unavailable", "Download service is temporarily unavailable");
        }
    }

    private async Task<Models.ComponentAsset?> FindPublishedAssetAsync(string version, string architecture,
        CancellationToken cancellationToken) =>
        (await db.ComponentAssets.AsNoTracking().Where(x => x.Component == Application && x.Version == version &&
                (x.Architecture == architecture || x.Architecture == "any")).ToListAsync(cancellationToken))
            .OrderBy(x => x.Architecture == architecture ? 0 : 1).FirstOrDefault();

    private ActionResult Missing(string code, string detail) =>
        ApiProblems.Create(HttpContext, StatusCodes.Status404NotFound, code, detail);

    private static bool HasTrustedMetadata(Models.ComponentAsset asset) =>
        asset.SizeBytes is > 0 && asset.Sha256?.Length == 64 && asset.Sha256.All(Uri.IsHexDigit);

    private static bool HasDownloadSource(Models.ComponentAsset asset) =>
        !string.IsNullOrWhiteSpace(asset.DownloadUrl) ||
        (!string.IsNullOrWhiteSpace(asset.YandexPublicKey) && !string.IsNullOrWhiteSpace(asset.YandexPath));
}
