using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HonestLicenseServer.Data;
using HonestLicenseServer.Contracts;
using HonestLicenseServer.Infrastructure;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController(HonestDbContext db, IConfiguration configuration,
    LicenseSignatureVerifier signatureVerifier) : ControllerBase
{
    [HttpGet("clients")]
    public async Task<IActionResult> Clients()
    {
        if (!IsAdmin()) return AdminUnauthorized();
        return Ok(await db.Clients.AsNoTracking().OrderBy(x => x.Name).Select(x => new
        {
            clientId = x.ExternalClientId, x.Name, x.Inn, x.Architecture, x.IsActive,
            x.HasLmDatabaseBackup, deviceCount = x.Devices.Count,
            activeDeviceCount = x.Devices.Count(d => d.Status == "Active"),
            licenseCount = x.Licenses.Count, credentialConfigured = x.Credentials.Any()
        }).ToListAsync());
    }

    [HttpGet("clients/{clientId}")]
    public async Task<IActionResult> Client(string clientId)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var value = await db.Clients.AsNoTracking().Where(x => x.ExternalClientId == clientId)
            .Select(x => new { clientId = x.ExternalClientId, x.Name, x.Inn, x.Architecture,
                x.IsActive, x.HasLmDatabaseBackup, x.CreatedAtUtc, x.UpdatedAtUtc,
                deviceCount = x.Devices.Count, licenseCount = x.Licenses.Count }).SingleOrDefaultAsync();
        return value is null ? NotFound(new { error = "client_not_found" }) : Ok(value);
    }

    [HttpPost("clients")]
    public async Task<IActionResult> CreateClient(CreateClientRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (await db.Clients.AnyAsync(x => x.ExternalClientId == request.ClientId) ||
            await db.Credentials.AnyAsync(x => x.Login == request.Login))
            return Conflict(new { error = "client_or_login_exists" });
        var now = DateTime.UtcNow;
        var client = new Client { ExternalClientId = request.ClientId, Name = request.Name,
            Inn = request.Inn, Architecture = request.Architecture, IsActive = true,
            HasLmDatabaseBackup = request.HasLmDatabaseBackup, CreatedAtUtc = now, UpdatedAtUtc = now };
        client.Credentials.Add(new Credential { Login = request.Login,
            PasswordHash = PasswordHasher.Hash(request.Password), IsActive = true, PasswordChangedAtUtc = now });
        SetInitialIntegrationSettings(client, request.Password, request.ChzToken);
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        AddAudit("Client.Created", "Client", client.Id.ToString(), client.Id, new { request.ClientId, request.Name });
        await db.SaveChangesAsync();
        return Created($"/api/admin/clients/{client.ExternalClientId}", new { client.Id, clientId = client.ExternalClientId });
    }

    [HttpPut("clients/{clientId}")]
    public async Task<IActionResult> UpdateClient(string clientId, UpdateClientRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var client = await db.Clients.SingleOrDefaultAsync(x => x.ExternalClientId == clientId);
        if (client is null) return NotFound(new { error = "client_not_found" });
        client.Name = request.Name; client.Inn = request.Inn; client.Architecture = request.Architecture;
        client.IsActive = request.IsActive; client.HasLmDatabaseBackup = request.HasLmDatabaseBackup;
        client.UpdatedAtUtc = DateTime.UtcNow;
        if (!request.IsActive)
        {
            var sessions = await db.RefreshTokens.Where(x => x.ClientId == client.Id && x.RevokedAtUtc == null).ToListAsync();
            foreach (var session in sessions) { session.RevokedAtUtc = DateTime.UtcNow; session.RevokeReason = "client_disabled"; }
        }
        AddAudit("Client.Updated", "Client", client.Id.ToString(), client.Id, new { request.IsActive });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("devices")]
    public async Task<IActionResult> Devices([FromQuery] string? clientId = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var query = db.Devices.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(clientId)) query = query.Where(x => x.Client.ExternalClientId == clientId);
        var devices = await query.OrderBy(x => x.Client.Name).ThenBy(x => x.Name).Select(x => new
        { x.Id, clientId = x.Client.ExternalClientId, clientName = x.Client.Name,
          storedDeviceId = x.ExternalDeviceId, x.Name, x.Address, x.Comment, x.Status, x.RegisteredAtUtc }).ToListAsync();
        return Ok(devices.Select(x =>
        {
            bool released = ReleasedDeviceIdentity.TryGetOriginal(x.Id, x.storedDeviceId, out string originalDeviceId);
            return new
            {
                x.Id,
                x.clientId,
                x.clientName,
                deviceId = released ? originalDeviceId : x.storedDeviceId,
                x.Name,
                x.Address,
                x.Comment,
                x.Status,
                x.RegisteredAtUtc,
                deviceIdReleased = released
            };
        }));
    }

    [HttpPost("devices")]
    public async Task<IActionResult> CreateDevice(CreateDeviceRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var client = await db.Clients.SingleOrDefaultAsync(x => x.ExternalClientId == request.ClientId);
        if (client is null) return NotFound(new { error = "client_not_found" });

        await using var transaction = await DeviceBindingGuard.BeginImmediateWriteAsync(db);
        if (await DeviceBindingGuard.ConflictsWithAnotherClientAsync(
                db, client.Id, request.DeviceId))
            return ApiProblems.Create(HttpContext, StatusCodes.Status409Conflict,
                DeviceBindingGuard.ErrorCode,
                "Device is already bound to another client");

        if (await db.Devices.AnyAsync(x => x.ClientId == client.Id && x.ExternalDeviceId == request.DeviceId))
            return Conflict(new { error = "device_exists" });
        var device = new Device { ClientId = client.Id, ExternalDeviceId = request.DeviceId,
            Name = request.Name, Address = request.Address, Comment = request.Comment,
            Status = "Active", RegisteredAtUtc = DateTime.UtcNow };
        db.Devices.Add(device); await db.SaveChangesAsync();
        AddAudit("Device.Created", "Device", device.Id.ToString(), client.Id, new { request.DeviceId });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Created($"/api/admin/devices/{device.Id}", new { device.Id });
    }

    [HttpPut("devices/{id:int}")]
    public async Task<IActionResult> UpdateDevice(int id, UpdateDeviceRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (request.Status is not ("Active" or "Disabled" or "Deleted")) return BadRequest(new { error = "invalid_device_status" });
        var device = await db.Devices.FindAsync(id);
        if (device is null) return NotFound(new { error = "device_not_found" });
        device.Name = request.Name; device.Address = request.Address; device.Comment = request.Comment; device.Status = request.Status;
        if (request.Status != "Active")
        {
            var sessions = await db.RefreshTokens.Where(x => x.DeviceId == id && x.RevokedAtUtc == null).ToListAsync();
            foreach (var session in sessions) { session.RevokedAtUtc = DateTime.UtcNow; session.RevokeReason = "device_disabled"; }
        }
        AddAudit("Device.Updated", "Device", id.ToString(), device.ClientId, new { request.Status });
        await db.SaveChangesAsync(); return NoContent();
    }

    [HttpPut("devices/{id:int}/restore")]
    public async Task<IActionResult> RestoreDevice(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        await using var transaction = await DeviceBindingGuard.BeginImmediateWriteAsync(db);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == id);
        if (device is null) return NotFound(new { error = "device_not_found" });
        if (device.Status != "Deleted") return Conflict(new { error = "device_not_deleted" });
        if (ReleasedDeviceIdentity.TryGetOriginal(device.Id, device.ExternalDeviceId, out _))
            return Conflict(new { error = "device_id_released" });
        if (await DeviceBindingGuard.ConflictsWithAnotherClientAsync(
                db, device.ClientId, device.ExternalDeviceId))
            return ApiProblems.Create(HttpContext, StatusCodes.Status409Conflict,
                DeviceBindingGuard.ErrorCode,
                "Device is already bound to another client");

        device.Status = "Active";
        AddAudit("Device.Restored", "Device", id.ToString(), device.ClientId,
            new { device.ExternalDeviceId });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return NoContent();
    }

    [HttpPut("devices/{id:int}/release")]
    public async Task<IActionResult> ReleaseDeviceId(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        await using var transaction = await DeviceBindingGuard.BeginImmediateWriteAsync(db);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == id);
        if (device is null) return NotFound(new { error = "device_not_found" });
        if (device.Status != "Deleted") return Conflict(new { error = "device_not_deleted" });
        if (ReleasedDeviceIdentity.TryGetOriginal(device.Id, device.ExternalDeviceId, out _))
            return Conflict(new { error = "device_id_already_released" });

        string oldExternalDeviceId = device.ExternalDeviceId;
        device.ExternalDeviceId = ReleasedDeviceIdentity.Create(device.Id, oldExternalDeviceId);
        var sessions = await db.RefreshTokens.Where(x => x.RevokedAtUtc == null &&
            (x.DeviceId == id ||
             (x.ClientId == device.ClientId && x.RequestedExternalDeviceId == oldExternalDeviceId)))
            .ToListAsync();
        foreach (var session in sessions)
        {
            session.RevokedAtUtc = DateTime.UtcNow;
            session.RevokeReason = "device_id_released";
        }
        AddAudit("Device.ExternalIdReleased", "Device", id.ToString(), device.ClientId,
            new { oldExternalDeviceId, archivedExternalDeviceId = device.ExternalDeviceId });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return NoContent();
    }

    [HttpGet("licenses")]
    public async Task<IActionResult> Licenses([FromQuery] string? clientId = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var query = db.Licenses.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(clientId)) query = query.Where(x => x.Client.ExternalClientId == clientId);
        var licenses = await query.OrderByDescending(x => x.Revision).Select(x => new
        { x.Id, clientId = x.Client.ExternalClientId, clientName = x.Client.Name,
          deviceDatabaseId = x.Device.Id, storedDeviceId = x.Device.ExternalDeviceId,
          x.Revision, x.KeyId, x.SignatureScope,
          x.SignatureVerifiedAtUtc, x.Status,
          x.IssuedAtUtc, x.ValidUntilUtc, hasSignature = x.SignatureBase64 != "" }).ToListAsync();
        return Ok(licenses.Select(x => new
        {
            x.Id,
            x.clientId,
            x.clientName,
            deviceId = ReleasedDeviceIdentity.TryGetOriginal(
                x.deviceDatabaseId, x.storedDeviceId, out string originalDeviceId)
                ? originalDeviceId
                : x.storedDeviceId,
            x.Revision,
            x.KeyId,
            x.SignatureScope,
            x.SignatureVerifiedAtUtc,
            x.Status,
            x.IssuedAtUtc,
            x.ValidUntilUtc,
            x.hasSignature
        }));
    }

    [HttpGet("licenses/{id:int}")]
    public async Task<IActionResult> License(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var value = await db.Licenses.AsNoTracking().Where(x => x.Id == id).Select(x => new
        { x.Id, clientId = x.Client.ExternalClientId, deviceDatabaseId = x.Device.Id,
          storedDeviceId = x.Device.ExternalDeviceId,
          x.Revision, x.GrantJson, x.SignatureBase64, x.KeyId, x.SignatureScope,
          x.SignatureVerifiedAtUtc, x.Status,
          x.IssuedAtUtc, x.ValidUntilUtc, x.PublishedAtUtc }).SingleOrDefaultAsync();
        if (value is null) return NotFound(new { error = "license_not_found" });
        string deviceId = ReleasedDeviceIdentity.TryGetOriginal(
            value.deviceDatabaseId, value.storedDeviceId, out string originalDeviceId)
            ? originalDeviceId
            : value.storedDeviceId;
        return Ok(new
        {
            value.Id, value.clientId, deviceId, value.Revision, value.GrantJson,
            value.SignatureBase64, value.KeyId, value.SignatureScope,
            value.SignatureVerifiedAtUtc, value.Status, value.IssuedAtUtc,
            value.ValidUntilUtc, value.PublishedAtUtc
        });
    }

    [HttpPost("licenses")]
    public async Task<IActionResult> PublishLicense(PublishLicenseRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        byte[] grantBytes;
        string grantJson;
        try
        {
            grantBytes = Convert.FromBase64String(request.GrantBase64);
            grantJson = new UTF8Encoding(false, true).GetString(grantBytes);
        }
        catch (FormatException) { return BadRequest(new { error = "invalid_grant_base64" }); }
        catch (DecoderFallbackException) { return BadRequest(new { error = "grant_is_not_valid_utf8" }); }
        byte[] signatureBytes;
        try { signatureBytes = Convert.FromBase64String(request.SignatureBase64); }
        catch (FormatException) { return BadRequest(new { error = "invalid_signature_base64" }); }
        GrantEnvelope? grant;
        try { grant = JsonSerializer.Deserialize<GrantEnvelope>(grantJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return BadRequest(new { error = "invalid_grant_json" }); }
        if (grant?.ClientId is null || grant.DeviceId is null || string.IsNullOrWhiteSpace(request.SignatureBase64))
            return BadRequest(new { error = "incomplete_signed_grant" });
        var keyId = request.KeyId ?? "primary-2026";
        var signatureResult = signatureVerifier.Verify(keyId, grantBytes, signatureBytes);
        if (signatureResult == SignatureVerificationResult.KeyNotConfigured)
            return ApiProblems.Create(HttpContext, StatusCodes.Status503ServiceUnavailable,
                "signing_key_not_configured", "License signing key is not configured");
        if (signatureResult == SignatureVerificationResult.InvalidPublicKey)
            return ApiProblems.Create(HttpContext, StatusCodes.Status503ServiceUnavailable,
                "invalid_signing_key", "Configured license signing key is invalid");
        if (signatureResult == SignatureVerificationResult.InvalidSignature)
            return ApiProblems.Create(HttpContext, StatusCodes.Status400BadRequest,
                "invalid_license_signature", "License signature is invalid");
        var client = await db.Clients.SingleOrDefaultAsync(x => x.ExternalClientId == grant.ClientId);
        if (client is null) return NotFound(new { error = "client_not_found" });
        var device = await db.Devices.SingleOrDefaultAsync(x => x.ClientId == client.Id && x.ExternalDeviceId == grant.DeviceId);
        if (device is null) return NotFound(new { error = "device_not_found" });
        if (await db.Licenses.AnyAsync(x => x.ClientId == client.Id && x.DeviceId == device.Id && x.Revision == grant.Revision))
            return Conflict(new { error = "license_revision_exists" });
        var active = await db.Licenses.Where(x => x.ClientId == client.Id && x.DeviceId == device.Id && x.Status == "Active").ToListAsync();
        foreach (var old in active) old.Status = "Superseded";
        var license = new License { ClientId = client.Id, DeviceId = device.Id, Revision = grant.Revision,
            GrantJson = grantJson, GrantBytes = grantBytes, SignatureBase64 = request.SignatureBase64,
            KeyId = keyId, SignatureScope = "PersonalGrant",
            SignatureVerifiedAtUtc = DateTime.UtcNow, Status = "Active",
            IssuedAtUtc = grant.IssuedAtUtc, ValidUntilUtc = grant.ValidUntilUtc, PublishedAtUtc = DateTime.UtcNow };
        db.Licenses.Add(license); await db.SaveChangesAsync();
        AddAudit("License.Published", "License", license.Id.ToString(), client.Id, new { grant.Revision, grant.DeviceId });
        await db.SaveChangesAsync(); return Created($"/api/admin/licenses/{license.Id}", new { license.Id });
    }

    [HttpGet("versions")]
    public async Task<IActionResult> Versions()
    {
        if (!IsAdmin()) return AdminUnauthorized();
        return Ok(await db.AppVersions.AsNoTracking().OrderBy(x => x.Application).ToListAsync());
    }

    [HttpPut("versions/{application}")]
    public async Task<IActionResult> PutVersion(string application, PutVersionRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var version = await db.AppVersions.SingleOrDefaultAsync(x => x.Application == application);
        if (version is null) { version = new AppVersion { Application = application, CurrentVersion = request.CurrentVersion, ImportedAtUtc = DateTime.UtcNow }; db.AppVersions.Add(version); }
        else { version.CurrentVersion = request.CurrentVersion; version.ImportedAtUtc = DateTime.UtcNow; }
        AddAudit("Version.Updated", "AppVersion", application, null, new { request.CurrentVersion });
        await db.SaveChangesAsync(); return NoContent();
    }

    [HttpGet("device-requests")]
    public async Task<IActionResult> DeviceRequests([FromQuery] string? status = "Pending")
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var query = db.DeviceRegistrationRequests.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        return Ok(await query.OrderBy(x => x.RequestedAtUtc).Select(x => new
        { x.Id, clientId = x.Client.ExternalClientId, clientName = x.Client.Name,
          clientInn = x.Client.Inn, deviceId = x.ExternalDeviceId,
          x.RequestedName, x.RequestedAddress,
          honestFlowVersion = x.RequestedHonestFlowVersion,
          x.Status, x.RequestedAtUtc, x.ResolvedAtUtc, x.Comment }).ToListAsync());
    }

    [HttpPut("device-requests/{id:int}/approve")]
    public async Task<IActionResult> ApproveDeviceRequest(int id, ResolveDeviceRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        await using var transaction = await DeviceBindingGuard.BeginImmediateWriteAsync(db);
        var pending = await db.DeviceRegistrationRequests.SingleOrDefaultAsync(x => x.Id == id);
        if (pending is null) return NotFound(new { error = "device_request_not_found" });
        if (pending.Status != "Pending") return Conflict(new { error = "device_request_already_resolved" });
        if (await DeviceBindingGuard.ConflictsWithAnotherClientAsync(
                db, pending.ClientId, pending.ExternalDeviceId))
            return ApiProblems.Create(HttpContext, StatusCodes.Status409Conflict,
                DeviceBindingGuard.ErrorCode,
                "Device is already bound to another client");

        var device = await db.Devices.SingleOrDefaultAsync(x => x.ClientId == pending.ClientId && x.ExternalDeviceId == pending.ExternalDeviceId);
        if (device is null)
        {
            device = new Device { ClientId = pending.ClientId, ExternalDeviceId = pending.ExternalDeviceId,
                Name = request.Name ?? pending.RequestedName,
                Address = request.Address ?? pending.RequestedAddress,
                Comment = request.Comment, Status = "Active", RegisteredAtUtc = DateTime.UtcNow };
            db.Devices.Add(device); await db.SaveChangesAsync();
        }
        else if (device.Status == "Deleted")
        {
            device.Name = request.Name ?? pending.RequestedName;
            device.Address = request.Address ?? pending.RequestedAddress;
            device.Comment = request.Comment;
            device.Status = "Active";
            device.RegisteredAtUtc = DateTime.UtcNow;
        }
        else
        {
            return Conflict(new { error = "device_already_registered" });
        }
        pending.Status = "Approved"; pending.ResolvedAtUtc = DateTime.UtcNow; pending.Comment = request.Comment;
        var sessions = await db.RefreshTokens.Where(x => x.ClientId == pending.ClientId &&
            x.RequestedExternalDeviceId == pending.ExternalDeviceId && x.DeviceId == null && x.RevokedAtUtc == null).ToListAsync();
        foreach (var session in sessions) session.DeviceId = device.Id;
        AddAudit("DeviceRequest.Approved", "DeviceRegistrationRequest", id.ToString(), pending.ClientId, new { device.Id });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Ok(new { device.Id });
    }

    [HttpPut("device-requests/{id:int}/reject")]
    public async Task<IActionResult> RejectDeviceRequest(int id, RejectDeviceRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var pending = await db.DeviceRegistrationRequests.SingleOrDefaultAsync(x => x.Id == id);
        if (pending is null) return NotFound(new { error = "device_request_not_found" });
        if (pending.Status != "Pending") return Conflict(new { error = "device_request_already_resolved" });
        pending.Status = "Rejected"; pending.ResolvedAtUtc = DateTime.UtcNow; pending.Comment = request.Comment;
        AddAudit("DeviceRequest.Rejected", "DeviceRegistrationRequest", id.ToString(), pending.ClientId, new { request.Comment });
        await db.SaveChangesAsync(); return NoContent();
    }

    [HttpGet("assets")]
    public async Task<IActionResult> Assets([FromQuery] string? component = null,
        [FromQuery] string? architecture = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var query = db.ComponentAssets.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(component))
            query = query.Where(x => x.Component == component);
        if (!string.IsNullOrWhiteSpace(architecture))
        {
            var normalized = AssetsController.NormalizeArchitecture(architecture);
            query = query.Where(x => x.Architecture == normalized);
        }
        return Ok(await query.OrderBy(x => x.Component).ThenByDescending(x => x.Version)
            .ThenBy(x => x.Architecture)
            .Select(x => new { x.Component, x.Version, x.Architecture, x.FileName,
                x.DownloadUrl, x.YandexPublicKey, x.YandexPath,
                x.Sha256, x.SizeBytes, x.UpdatedAtUtc }).ToListAsync());
    }

    [HttpPut("assets/{component}/{version}")]
    public async Task<IActionResult> PutAsset(string component, string version,
        PutComponentAssetRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var architecture = AssetsController.NormalizeArchitecture(request.Architecture);
        var hasDirectUrl = !string.IsNullOrWhiteSpace(request.DownloadUrl);
        var hasYandex = !string.IsNullOrWhiteSpace(request.YandexPublicKey) &&
            !string.IsNullOrWhiteSpace(request.YandexPath);
        if (!hasDirectUrl && !hasYandex)
            return BadRequest(new { error = "asset_download_source_required" });
        if ((!string.IsNullOrWhiteSpace(request.YandexPublicKey) ||
             !string.IsNullOrWhiteSpace(request.YandexPath)) && !hasYandex)
            return BadRequest(new { error = "incomplete_yandex_asset_source" });
        var asset = await db.ComponentAssets.SingleOrDefaultAsync(x =>
            x.Component == component && x.Version == version && x.Architecture == architecture);
        if (asset is null)
        {
            asset = new ComponentAsset { Component = component, Version = version,
                Architecture = architecture, FileName = request.FileName,
                DownloadUrl = Clean(request.DownloadUrl),
                YandexPublicKey = Clean(request.YandexPublicKey),
                YandexPath = Clean(request.YandexPath),
                Sha256 = request.Sha256?.ToLowerInvariant(), SizeBytes = request.SizeBytes,
                UpdatedAtUtc = DateTime.UtcNow };
            db.ComponentAssets.Add(asset);
        }
        else
        {
            asset.FileName = request.FileName;
            asset.DownloadUrl = Clean(request.DownloadUrl);
            asset.YandexPublicKey = Clean(request.YandexPublicKey);
            asset.YandexPath = Clean(request.YandexPath);
            asset.Sha256 = request.Sha256?.ToLowerInvariant();
            asset.SizeBytes = request.SizeBytes;
            asset.UpdatedAtUtc = DateTime.UtcNow;
        }
        AddAudit("Asset.Updated", "ComponentAsset", $"{component}:{version}", null,
            new { component, version, architecture, request.FileName });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("clients/{clientId}/component-versions")]
    public async Task<IActionResult> ComponentOverrides(string clientId)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (!await db.Clients.AnyAsync(x => x.ExternalClientId == clientId))
            return NotFound(new { error = "client_not_found" });
        return Ok(await db.ClientComponentVersions.AsNoTracking()
            .Where(x => x.Client.ExternalClientId == clientId)
            .OrderBy(x => x.Component)
            .Select(x => new { x.Component, x.RequiredVersion, x.UpdatedAtUtc }).ToListAsync());
    }

    [HttpGet("clients/{clientId}/integration-settings")]
    public async Task<IActionResult> IntegrationSettings(string clientId)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var value = await db.Clients.AsNoTracking()
            .Where(x => x.ExternalClientId == clientId)
            .Select(x => new ClientIntegrationSettingsResponse(
                x.ExternalClientId,
                db.ClientSettings.Where(s => s.ClientId == x.Id)
                    .Select(s => s.IdentificationCode).SingleOrDefault(),
                db.ClientSettings.Where(s => s.ClientId == x.Id)
                    .Select(s => s.ChzToken).SingleOrDefault(),
                db.ClientSettings.Where(s => s.ClientId == x.Id)
                    .Select(s => s.RuDesktopEnabled).SingleOrDefault(),
                db.ClientSettings.Where(s => s.ClientId == x.Id)
                    .Select(s => s.RuDesktopAutoOfferPasswordSetup).SingleOrDefault(),
                db.ClientSettings.Any(s => s.ClientId == x.Id &&
                    s.IdentificationCode != null && s.ChzToken != null)))
            .SingleOrDefaultAsync();
        return value is null ? NotFound(new { error = "client_not_found" }) : Ok(value);
    }

    [HttpGet("clients/{clientId}/license-policy")]
    public async Task<IActionResult> LicensePolicy(string clientId)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var value = await db.Clients.AsNoTracking()
            .Where(x => x.ExternalClientId == clientId)
            .Select(x => new ClientLicensePolicyResponse(
                x.ExternalClientId,
                db.LicensePolicies.Where(p => p.ClientId == x.Id)
                    .Select(p => (bool?)p.IsEnabled).SingleOrDefault()))
            .SingleOrDefaultAsync();
        return value is null ? NotFound(new { error = "client_not_found" }) : Ok(value);
    }

    [HttpPut("clients/{clientId}/license-policy")]
    public async Task<IActionResult> PutLicensePolicy(string clientId,
        PutClientLicensePolicyRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var client = await db.Clients.SingleOrDefaultAsync(x => x.ExternalClientId == clientId);
        if (client is null) return NotFound(new { error = "client_not_found" });

        var policy = await db.LicensePolicies.SingleOrDefaultAsync(x => x.ClientId == client.Id);
        bool? previousValue = policy?.IsEnabled;
        bool created = policy is null;
        if (policy is null)
        {
            policy = new LicensePolicy
            {
                ClientId = client.Id,
                IsEnabled = request.IsEnabled,
                MinimumHonestFlowVersion = null,
                OfflineGraceHours = 0,
                SourceRevision = 0,
                SourceIssuedAtUtc = DateTime.UtcNow,
                SourceValidUntilUtc = DateTime.MaxValue
            };
            db.LicensePolicies.Add(policy);
        }
        else
        {
            policy.IsEnabled = request.IsEnabled;
        }

        AddAudit("ClientLicensePolicy.Updated", "LicensePolicy", client.Id.ToString(), client.Id,
            new { previousIsEnabled = previousValue, request.IsEnabled, created });
        await db.SaveChangesAsync();
        return Ok(new ClientLicensePolicyResponse(client.ExternalClientId, policy.IsEnabled));
    }

    [HttpPut("clients/{clientId}/integration-settings")]
    public async Task<IActionResult> PutIntegrationSettings(string clientId,
        PutClientIntegrationSettingsRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var client = await db.Clients.Include(x => x.Credentials)
            .SingleOrDefaultAsync(x => x.ExternalClientId == clientId);
        if (client is null) return NotFound(new { error = "client_not_found" });

        var settings = await db.ClientSettings.SingleOrDefaultAsync(x => x.ClientId == client.Id);
        if (settings is null)
        {
            settings = new ClientSetting { ClientId = client.Id };
            db.ClientSettings.Add(settings);
        }
        settings.IdentificationCode = request.IdentificationCode.Trim();
        settings.ChzToken = Clean(request.ChzToken);
        settings.RuDesktopEnabled = request.RuDesktopEnabled;
        settings.RuDesktopAutoOfferPasswordSetup = request.RuDesktopAutoOfferPasswordSetup;
        foreach (var credential in client.Credentials.Where(x => x.IsActive))
        {
            credential.PasswordHash = PasswordHasher.Hash(settings.IdentificationCode);
            credential.PasswordChangedAtUtc = DateTime.UtcNow;
        }
        client.UpdatedAtUtc = DateTime.UtcNow;
        AddAudit("ClientIntegrationSettings.Updated", "Client", client.Id.ToString(), client.Id,
            new { identificationCodeChanged = true, chzTokenChanged = true });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("clients/{clientId}/component-versions/{component}")]
    public async Task<IActionResult> PutComponentOverride(string clientId, string component,
        PutComponentOverrideRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var client = await db.Clients.SingleOrDefaultAsync(x => x.ExternalClientId == clientId);
        if (client is null) return NotFound(new { error = "client_not_found" });
        var current = await db.ClientComponentVersions.SingleOrDefaultAsync(x =>
            x.ClientId == client.Id && x.Component == component);
        var requiredVersion = string.IsNullOrWhiteSpace(request.RequiredVersion)
            ? null : request.RequiredVersion.Trim();

        if (requiredVersion is null)
        {
            if (current is not null) db.ClientComponentVersions.Remove(current);
        }
        else
        {
            if (!await db.ComponentAssets.AnyAsync(x =>
                    x.Component == component && x.Version == requiredVersion))
                return NotFound(new { error = "component_asset_not_found" });
            if (current is null)
            {
                current = new ClientComponentVersion { ClientId = client.Id,
                    Component = component, RequiredVersion = requiredVersion,
                    UpdatedAtUtc = DateTime.UtcNow };
                db.ClientComponentVersions.Add(current);
            }
            else
            {
                current.RequiredVersion = requiredVersion;
                current.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        AddAudit("ComponentOverride.Updated", "Client", client.Id.ToString(), client.Id,
            new { component, requiredVersion });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("licenses/{id:int}/revoke")]
    public async Task<IActionResult> RevokeLicense(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var license = await db.Licenses.FindAsync(id);
        if (license is null) return NotFound(new { error = "license_not_found" });
        if (license.Status == "Revoked") return NoContent();
        if (license.Status != "Active") return Conflict(new { error = "license_not_active" });
        license.Status = "Revoked";
        AddAudit("License.Revoked", "License", license.Id.ToString(), license.ClientId,
            new { license.Revision, license.DeviceId });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("support-requests")]
    public async Task<IActionResult> SupportRequests([FromQuery] string? status = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var query = db.SupportRequests.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        return Ok(await query.OrderByDescending(x => x.CreatedAtUtc).Select(x => new
        {
            x.Id, clientId = x.Client.ExternalClientId, x.ExternalDeviceId,
            x.Subject, x.Message, x.Contact, x.HonestFlowVersion, x.Status, x.CreatedAtUtc
        }).ToListAsync());
    }

    [HttpPut("support-requests/{id:int}/resolve")]
    public async Task<IActionResult> ResolveSupportRequest(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var request = await db.SupportRequests.SingleOrDefaultAsync(x => x.Id == id);
        if (request is null) return NotFound(new { error = "support_request_not_found" });
        if (request.Status is "Resolved" or "Closed") return NoContent();
        request.Status = "Resolved";
        AddAudit("SupportRequest.Resolved", "SupportRequest", id.ToString(), request.ClientId, new { });
        await db.SaveChangesAsync();
        return NoContent();
    }

    private bool IsAdmin()
    {
        var expected = configuration["AdminApi:Key"];
        var provided = Request.Headers["X-Admin-Key"].ToString();
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided)) return false;
        var a = Encoding.UTF8.GetBytes(expected); var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
    private IActionResult AdminUnauthorized() => Unauthorized(new { error = "invalid_admin_key" });
    private void AddAudit(string action, string type, string entityId, int? clientId, object details) =>
        db.AuditEvents.Add(new AuditEvent { OccurredAtUtc = DateTime.UtcNow, Action = action,
            EntityType = type, EntityId = entityId, ClientId = clientId,
            DetailsJson = JsonSerializer.Serialize(details), IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CorrelationId = HttpContext.TraceIdentifier });

    private static void SetInitialIntegrationSettings(Client client, string identificationCode, string? chzToken)
    {
        // EF sets ClientId when the new client graph is saved.
        client.Settings = new ClientSetting
        {
            IdentificationCode = identificationCode.Trim(),
            ChzToken = string.IsNullOrWhiteSpace(chzToken) ? null : chzToken.Trim()
        };
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public record CreateClientRequest(string ClientId, string Name, string Login, string Password,
    string? Inn, string? Architecture, bool HasLmDatabaseBackup = false, string? ChzToken = null);
public record UpdateClientRequest(string Name, string? Inn, string? Architecture, bool IsActive, bool HasLmDatabaseBackup);
public record CreateDeviceRequest(string ClientId, string DeviceId, string Name, string? Address, string? Comment);
public record UpdateDeviceRequest(string Name, string? Address, string? Comment, string Status);
public record PublishLicenseRequest(string GrantBase64, string SignatureBase64, string? KeyId);
public record PutVersionRequest(string CurrentVersion);
public record ResolveDeviceRequest(string? Name, string? Address, string? Comment);
public record RejectDeviceRequest(string? Comment);
public record GrantEnvelope(long Revision, string? ClientId, string? DeviceId, DateTime IssuedAtUtc, DateTime ValidUntilUtc);
