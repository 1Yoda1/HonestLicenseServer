using System.Text.Json;
using HonestLicenseServer.Authentication;
using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/service/install-access")]
public sealed class ServiceInstallationController(
    HonestDbContext db,
    ServiceInstallTokenStore tokens) : ControllerBase
{
    private static readonly HashSet<string> InstallableComponents =
        new(StringComparer.OrdinalIgnoreCase) { "LmModule", "AtolDriver", "ESM", "Controller" };

    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("service-install")]
    [ProducesResponseType<ServiceInstallAccessResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ServiceInstallAccessResponse>> Authorize(
        ServiceInstallAccessRequest request,
        CancellationToken cancellationToken)
    {
        ServiceInstallationAccess? access = await db.ServiceInstallationAccess
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (access is null || !access.IsEnabled)
        {
            AddAudit("ServiceInstallAccess.Denied", "disabled", request);
            await db.SaveChangesAsync(cancellationToken);
            return ApiProblems.Create(HttpContext, StatusCodes.Status403Forbidden,
                "service_install_access_disabled", "Service installation access is disabled");
        }

        if (!PasswordHasher.Verify(request.Password, access.PasswordHash))
        {
            AddAudit("ServiceInstallAccess.Denied", "invalid_password", request);
            await db.SaveChangesAsync(cancellationToken);
            return ApiProblems.Create(HttpContext, StatusCodes.Status401Unauthorized,
                "invalid_service_install_password", "Invalid service installation password");
        }

        string architecture = AssetsController.NormalizeArchitecture(request.Architecture);
        ServiceInstallToken token = tokens.Issue(architecture);
        IReadOnlyList<ServiceInstallComponentResponse> components = await BuildComponentsAsync(
            architecture, cancellationToken);
        AddAudit("ServiceInstallAccess.Granted", "granted", request);
        await db.SaveChangesAsync(cancellationToken);
        int expiresIn = Math.Max(1, (int)Math.Floor((token.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds));
        return Ok(new ServiceInstallAccessResponse(
            token.Value, expiresIn, ServiceInstallOnlyDefaults.Scope, components));
    }

    private async Task<IReadOnlyList<ServiceInstallComponentResponse>> BuildComponentsAsync(
        string architecture,
        CancellationToken cancellationToken)
    {
        List<AppVersion> versions = await db.AppVersions.AsNoTracking()
            .Where(x => InstallableComponents.Contains(x.Application))
            .OrderBy(x => x.Application)
            .ToListAsync(cancellationToken);
        List<ComponentAsset> assets = await db.ComponentAssets.AsNoTracking()
            .Where(x => InstallableComponents.Contains(x.Component))
            .ToListAsync(cancellationToken);
        return versions.Select(version =>
        {
            ComponentAsset? asset = assets.Where(x =>
                    string.Equals(x.Component, version.Application, StringComparison.OrdinalIgnoreCase) &&
                    x.Version == version.CurrentVersion &&
                    (x.Architecture == architecture || x.Architecture == "any"))
                .OrderBy(x => x.Architecture == architecture ? 0 : 1)
                .FirstOrDefault();
            string? downloadUrl = asset is null ? null :
                $"{Request.Scheme}://{Request.Host}/api/assets/install/" +
                $"{Uri.EscapeDataString(asset.Component)}/{Uri.EscapeDataString(asset.Version)}/download";
            return new ServiceInstallComponentResponse(version.Application, version.CurrentVersion,
                asset?.FileName, downloadUrl, asset?.Sha256, asset?.SizeBytes, asset?.Architecture);
        }).ToArray();
    }

    private void AddAudit(string action, string outcome, ServiceInstallAccessRequest request) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAtUtc = DateTime.UtcNow,
            Action = action,
            EntityType = "ServiceInstallationAccess",
            EntityId = "global",
            DetailsJson = JsonSerializer.Serialize(new
            {
                outcome,
                appVersion = Clean(request.AppVersion),
                architecture = Clean(request.Architecture)
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CorrelationId = HttpContext.TraceIdentifier
        });

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
