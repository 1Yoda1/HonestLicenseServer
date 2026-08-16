using HonestLicenseServer.Authentication;
using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/configuration")]
public sealed class ConfigurationController(HonestDbContext db) : ControllerBase
{
    [HttpGet("current")]
    [Authorize(Policy = OpaqueBearerDefaults.ActiveDevicePolicy)]
    [ProducesResponseType<ConfigurationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ConfigurationResponse>> Current()
    {
        var clientId = User.ClientId();
        var deviceId = User.DeviceId()!.Value;

        var client = await db.Clients.AsNoTracking().SingleAsync(x => x.Id == clientId);
        var device = await db.Devices.AsNoTracking()
            .SingleAsync(x => x.Id == deviceId && x.ClientId == clientId);
        var settings = await db.ClientSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ClientId == clientId);
        var policy = await db.LicensePolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ClientId == clientId);
        var globalVersions = await db.AppVersions.AsNoTracking()
            .OrderBy(x => x.Application).ToListAsync();
        var overrides = await db.ClientComponentVersions.AsNoTracking()
            .Where(x => x.ClientId == clientId).ToListAsync();
        var assets = await db.ComponentAssets.AsNoTracking().ToListAsync();

        var overrideByComponent = overrides.ToDictionary(x => x.Component,
            StringComparer.OrdinalIgnoreCase);
        var normalizedArchitecture = AssetsController.NormalizeArchitecture(client.Architecture);
        var components = globalVersions.Select(global =>
        {
            overrideByComponent.TryGetValue(global.Application, out var componentOverride);
            var overrideVersion = string.IsNullOrWhiteSpace(componentOverride?.RequiredVersion)
                ? null : componentOverride.RequiredVersion;
            var effectiveVersion = overrideVersion ?? global.CurrentVersion;
            var asset = assets.Where(x =>
                    string.Equals(x.Component, global.Application, StringComparison.OrdinalIgnoreCase) &&
                    x.Version == effectiveVersion &&
                    (x.Architecture == normalizedArchitecture || x.Architecture == "any"))
                .OrderBy(x => x.Architecture == normalizedArchitecture ? 0 : 1)
                .FirstOrDefault();
            var downloadUrl = asset is null ? null :
                $"{Request.Scheme}://{Request.Host}/api/assets/" +
                $"{Uri.EscapeDataString(asset.Component)}/{Uri.EscapeDataString(asset.Version)}/download";
            return new ComponentConfiguration(global.Application, global.CurrentVersion,
                overrideVersion, effectiveVersion, asset?.FileName, downloadUrl,
                asset?.Sha256, asset?.SizeBytes, asset?.Architecture, overrideVersion is not null);
        }).ToList();

        var revisionCandidates = new List<DateTime> { client.UpdatedAtUtc, device.RegisteredAtUtc };
        revisionCandidates.AddRange(globalVersions.Select(x => x.ImportedAtUtc));
        revisionCandidates.AddRange(overrides.Select(x => x.UpdatedAtUtc));
        revisionCandidates.AddRange(assets.Select(x => x.UpdatedAtUtc));
        var revision = DateTime.SpecifyKind(revisionCandidates.Max(), DateTimeKind.Utc);

        return Ok(new ConfigurationResponse(
            revision,
            new ClientConfiguration(client.ExternalClientId, client.Name, client.Inn, client.Architecture,
                client.HasLmDatabaseBackup, settings?.RuDesktopEnabled ?? false,
                settings?.RuDesktopAutoOfferPasswordSetup ?? false,
                settings?.IdentificationCode, settings?.ChzToken),
            new DeviceConfiguration(device.ExternalDeviceId, device.Name, device.Address, device.Status),
            policy is null ? null : new LicensePolicyConfiguration(policy.IsEnabled,
                policy.MinimumHonestFlowVersion, policy.OfflineGraceHours,
                policy.SourceRevision, policy.SourceValidUntilUtc),
            components));
    }
}
