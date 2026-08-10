using HonestLicenseServer.Authentication;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/assets")]
public sealed class AssetsController(
    HonestDbContext db,
    IYandexPublicDownloadResolver yandex,
    ILogger<AssetsController> logger) : ControllerBase
{
    [HttpGet("{component}/{version}/download")]
    [Authorize(Policy = OpaqueBearerDefaults.ActiveDevicePolicy)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Download(string component, string version,
        CancellationToken cancellationToken)
    {
        var clientId = User.ClientId();
        var architecture = await db.Clients.AsNoTracking()
            .Where(x => x.Id == clientId)
            .Select(x => x.Architecture)
            .SingleAsync(cancellationToken);
        var normalizedArchitecture = NormalizeArchitecture(architecture);
        var candidates = await db.ComponentAssets.AsNoTracking()
            .Where(x => x.Component == component && x.Version == version &&
                (x.Architecture == normalizedArchitecture || x.Architecture == "any"))
            .ToListAsync(cancellationToken);
        var asset = candidates
            .OrderBy(x => x.Architecture == normalizedArchitecture ? 0 : 1)
            .FirstOrDefault();
        if (asset is null)
            return ApiProblems.Create(HttpContext, StatusCodes.Status404NotFound,
                "component_asset_not_found", "Component asset was not found");

        if (!string.IsNullOrWhiteSpace(asset.DownloadUrl))
            return Redirect(asset.DownloadUrl);
        if (string.IsNullOrWhiteSpace(asset.YandexPublicKey) ||
            string.IsNullOrWhiteSpace(asset.YandexPath))
            return ApiProblems.Create(HttpContext, StatusCodes.Status404NotFound,
                "component_asset_download_not_configured", "Component asset download is not configured");

        try
        {
            var href = await yandex.ResolveAsync(asset.YandexPublicKey,
                asset.YandexPath, cancellationToken);
            return href is null
                ? ApiProblems.Create(HttpContext, StatusCodes.Status502BadGateway,
                    "yandex_download_link_unavailable", "Download link is unavailable")
                : Redirect(href);
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception,
                "Could not resolve Yandex download for {Component} {Version} {Architecture}",
                component, version, normalizedArchitecture);
            return ApiProblems.Create(HttpContext, StatusCodes.Status502BadGateway,
                "yandex_download_unavailable", "Download service is temporarily unavailable");
        }
    }

    public static string NormalizeArchitecture(string? architecture) =>
        architecture?.Trim().ToLowerInvariant() switch
        {
            "x86" or "x32" or "win32" => "x86",
            "x64" or "win64" or "amd64" => "x64",
            "arm64" => "arm64",
            _ => "any"
        };
}
