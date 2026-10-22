using HonestLicenseServer.Authentication;
using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Models;
using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/support")]
public sealed class SupportController(
    HonestDbContext db,
    IDiagnosticObjectStorage diagnosticStorage,
    IOptions<DiagnosticStorageOptions> diagnosticOptions) : ControllerBase
{
    [HttpGet("remote-access/key")]
    [Authorize(Policy = OpaqueBearerDefaults.ServicePolicy)]
    public async Task<ActionResult<RemoteAccessKeyResponse>> GetRemoteAccessKey()
    {
        var key = await db.RemoteAccessKeys.AsNoTracking().OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        if (key is null) return NotFound();
        return new RemoteAccessKeyResponse(key.KeyId, key.PublicKeyBase64,
            key.SignatureBase64, key.SigningKeyId);
    }

    [HttpGet("remote-access/current")]
    [Authorize(Policy = OpaqueBearerDefaults.ServicePolicy)]
    public async Task<ActionResult<RemoteAccessCredentialStatusResponse>> GetRemoteAccessStatus()
    {
        if (User.DeviceId() is not int deviceId) return Forbid();
        var value = await db.RemoteAccessCredentials.AsNoTracking()
            .SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.ClientId == User.ClientId());
        var activeRequest = await db.SupportRequests.AsNoTracking()
            .Where(x => x.DeviceId == deviceId && x.ClientId == User.ClientId() &&
                        x.RemoteAccessCredentialId != null && x.Status == "Accepted")
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new { x.Id, x.CreatedAtUtc })
            .FirstOrDefaultAsync();
        return new RemoteAccessCredentialStatusResponse(value?.IsActive == true,
            value?.IsActive == true ? value.CredentialId : null,
            value?.IsActive == true ? value.RuDesktopId : null,
            value?.IsActive == true ? value.KeyId : null,
            activeRequest?.Id, activeRequest?.CreatedAtUtc);
    }

    [HttpPut("remote-access/current")]
    [Authorize(Policy = OpaqueBearerDefaults.ServicePolicy)]
    public async Task<IActionResult> PutRemoteAccessCredential(PutRemoteAccessCredentialRequest request)
    {
        if (User.DeviceId() is not int deviceId) return Forbid();
        if (!Guid.TryParse(request.CredentialId, out _) ||
            request.RuDesktopId.Length is < 3 or > 100 ||
            request.CiphertextBase64.Length > 2048)
            return BadRequest(new { error = "invalid_remote_credential" });
        await using var transaction = await db.Database.BeginTransactionAsync();
        var key = await db.RemoteAccessKeys.OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        if (key is null || key.KeyId != request.KeyId)
            return Conflict(new { error = "remote_access_key_changed" });
        try
        {
            if (Convert.FromBase64String(request.CiphertextBase64).Length != 384)
                return BadRequest(new { error = "invalid_ciphertext" });
        }
        catch (FormatException)
        {
            return BadRequest(new { error = "invalid_ciphertext" });
        }
        var current = await db.RemoteAccessCredentials.SingleOrDefaultAsync(x => x.DeviceId == deviceId);
        if (current is not null && current.CredentialId == request.CredentialId)
            return current.IsActive && current.ClientId == User.ClientId() &&
                   current.KeyId == request.KeyId && current.RuDesktopId == request.RuDesktopId &&
                   current.CiphertextBase64 == request.CiphertextBase64
                ? NoContent() : Conflict(new { error = "credential_id_already_used" });
        if (current is null)
        {
            current = new RemoteAccessCredential { DeviceId = deviceId, ClientId = User.ClientId(),
                CredentialId = request.CredentialId, KeyId = request.KeyId,
                RuDesktopId = request.RuDesktopId, CiphertextBase64 = request.CiphertextBase64,
                IsActive = true, UpdatedAtUtc = DateTime.UtcNow };
            db.RemoteAccessCredentials.Add(current);
        }
        else
        {
            if (current.ClientId != User.ClientId()) return Forbid();
            current.CredentialId = request.CredentialId;
            current.KeyId = request.KeyId;
            current.RuDesktopId = request.RuDesktopId;
            current.CiphertextBase64 = request.CiphertextBase64;
            current.IsActive = true;
            current.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SupportRequests.Where(x => x.DeviceId == deviceId &&
                x.RemoteAccessCredentialId != null && x.Status == "Accepted")
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, "Cancelled"));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return NoContent();
    }

    [HttpDelete("remote-access/current")]
    [Authorize(Policy = OpaqueBearerDefaults.ServicePolicy)]
    public async Task<IActionResult> RevokeRemoteAccessCredential()
    {
        if (User.DeviceId() is not int deviceId) return Forbid();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.RemoteAccessCredentials.Where(x => x.DeviceId == deviceId && x.ClientId == User.ClientId())
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.IsActive, false));
        await db.SupportRequests.Where(x => x.DeviceId == deviceId &&
                x.RemoteAccessCredentialId != null && x.Status == "Accepted")
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, "Cancelled"));
        await transaction.CommitAsync();
        return NoContent();
    }

    [HttpPost("remote-requests")]
    [Authorize(Policy = OpaqueBearerDefaults.ServicePolicy)]
    [EnableRateLimiting("support")]
    public async Task<ActionResult<SupportRequestAcceptedResponse>> CreateRemoteRequest(CreateRemoteSupportRequest request)
    {
        if (User.DeviceId() is not int deviceId) return Forbid();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var credential = await db.RemoteAccessCredentials.AsNoTracking().SingleOrDefaultAsync(x =>
            x.DeviceId == deviceId && x.ClientId == User.ClientId() && x.IsActive &&
            x.CredentialId == request.CredentialId);
        if (credential is null) return Conflict(new { error = "remote_access_not_ready" });
        var existing = await db.SupportRequests.AsNoTracking()
            .Where(x => x.DeviceId == deviceId && x.ClientId == User.ClientId() &&
                        x.RemoteAccessCredentialId != null && x.Status == "Accepted")
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (existing is not null)
        {
            await transaction.CommitAsync();
            return Accepted(new SupportRequestAcceptedResponse(
                existing.Id, existing.Status, existing.CreatedAtUtc));
        }
        var item = new SupportRequest
        {
            ClientId = User.ClientId(), DeviceId = deviceId,
            ExternalDeviceId = User.ExternalDeviceId(), Subject = request.Subject.Trim(),
            Message = request.Message.Trim(), Contact = request.Contact.Trim(),
            HonestFlowVersion = request.HonestFlowVersion?.Trim(),
            RemoteAccessCredentialId = credential.CredentialId,
            RuDesktopId = credential.RuDesktopId, Status = "Accepted", CreatedAtUtc = DateTime.UtcNow
        };
        db.SupportRequests.Add(item);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Accepted(new SupportRequestAcceptedResponse(item.Id, item.Status, item.CreatedAtUtc));
    }

    [HttpPost("requests")]
    [Authorize(Policy = OpaqueBearerDefaults.ServicePolicy)]
    [EnableRateLimiting("support")]
    [ProducesResponseType<SupportRequestAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SupportRequestAcceptedResponse>> Create(CreateSupportRequest request)
    {
        var item = new SupportRequest
        {
            ClientId = User.ClientId(), DeviceId = User.DeviceId(),
            ExternalDeviceId = User.ExternalDeviceId(), Subject = request.Subject.Trim(),
            Message = request.Message.Trim(), Contact = request.Contact.Trim(),
            HonestFlowVersion = request.HonestFlowVersion?.Trim(), Status = "Accepted",
            CreatedAtUtc = DateTime.UtcNow
        };
        db.SupportRequests.Add(item);
        await db.SaveChangesAsync();
        return Accepted(new SupportRequestAcceptedResponse(item.Id, item.Status, item.CreatedAtUtc));
    }

    [HttpPost("diagnostics/uploads")]
    [Authorize(Policy = OpaqueBearerDefaults.ServicePolicy)]
    [EnableRateLimiting("support")]
    public async Task<ActionResult<DiagnosticUploadResponse>> CreateDiagnosticUpload(
        CreateDiagnosticUploadRequest request, CancellationToken cancellationToken)
    {
        if (!diagnosticStorage.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "diagnostic_storage_unavailable" });
        if (User.DeviceId() is not int deviceId) return Forbid();

        string fileName = Path.GetFileName(request.FileName.Trim());
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "diagnostic_archive_must_be_zip" });

        int clientId = User.ClientId();
        var device = await db.Devices.AsNoTracking()
            .Include(x => x.Client)
            .SingleOrDefaultAsync(x => x.Id == deviceId && x.ClientId == clientId && x.Status == "Active",
                cancellationToken);
        if (device is null) return Forbid();

        DateTime now = DateTime.UtcNow;
        DateTime uploadUrlExpiresAtUtc = now.AddMinutes(Math.Clamp(diagnosticOptions.Value.UploadUrlMinutes, 5, 60));
        DateTime archiveExpiresAtUtc = now.AddHours(Math.Clamp(diagnosticOptions.Value.RetentionHours, 1, 168));
        string objectKey = $"diagnostics/{now:yyyy/MM}/{Guid.NewGuid():N}.zip";
        var archive = new DiagnosticArchive
        {
            ClientId = clientId,
            DeviceId = deviceId,
            ExternalDeviceId = device.ExternalDeviceId,
            ClientName = device.Client.Name,
            DeviceName = device.Name,
            Address = device.Address?.Trim() ?? string.Empty,
            FileName = fileName,
            ObjectKey = objectKey,
            SizeBytes = request.SizeBytes,
            Sha256 = request.Sha256.ToUpperInvariant(),
            HonestFlowVersion = request.HonestFlowVersion?.Trim(),
            Status = "Pending",
            CreatedAtUtc = now,
            UploadUrlExpiresAtUtc = uploadUrlExpiresAtUtc,
            ExpiresAtUtc = archiveExpiresAtUtc
        };
        db.DiagnosticArchives.Add(archive);
        await db.SaveChangesAsync(cancellationToken);

        return Ok(new DiagnosticUploadResponse(
            archive.Id,
            diagnosticStorage.CreateUploadUrl(objectKey, uploadUrlExpiresAtUtc),
            uploadUrlExpiresAtUtc,
            archiveExpiresAtUtc));
    }

    [HttpPost("diagnostics/{id:int}/complete")]
    [Authorize(Policy = OpaqueBearerDefaults.ServicePolicy)]
    public async Task<IActionResult> CompleteDiagnosticUpload(
        int id, CompleteDiagnosticUploadRequest request, CancellationToken cancellationToken)
    {
        if (!diagnosticStorage.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "diagnostic_storage_unavailable" });
        if (User.DeviceId() is not int deviceId) return Forbid();
        var archive = await db.DiagnosticArchives.SingleOrDefaultAsync(x => x.Id == id &&
            x.ClientId == User.ClientId() && x.DeviceId == deviceId, cancellationToken);
        if (archive is null) return NotFound(new { error = "diagnostic_archive_not_found" });
        if (archive.Status is "Available" or "Downloaded") return NoContent();
        if (archive.Status != "Pending") return Conflict(new { error = "diagnostic_archive_not_pending" });
        if (request.SizeBytes != archive.SizeBytes ||
            !string.Equals(request.Sha256, archive.Sha256, StringComparison.OrdinalIgnoreCase))
            return Conflict(new { error = "diagnostic_archive_metadata_mismatch" });

        DiagnosticObjectInfo? info = await diagnosticStorage.GetInfoAsync(archive.ObjectKey, cancellationToken);
        if (info is null) return Conflict(new { error = "diagnostic_archive_upload_incomplete" });
        if (info.SizeBytes != archive.SizeBytes)
            return Conflict(new { error = "diagnostic_archive_size_mismatch" });

        DateTime now = DateTime.UtcNow;
        archive.Status = "Available";
        archive.UploadedAtUtc = now;
        archive.ExpiresAtUtc = now.AddHours(Math.Clamp(diagnosticOptions.Value.RetentionHours, 1, 168));
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
