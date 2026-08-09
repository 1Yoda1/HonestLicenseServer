using HonestLicenseServer.Data;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(HonestDbContext db) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var credential = await db.Credentials.Include(x => x.Client)
            .SingleOrDefaultAsync(x => x.Login == request.Login && x.IsActive);
        if (credential is null || !credential.Client.IsActive ||
            !PasswordHasher.Verify(request.Password, credential.PasswordHash))
            return Unauthorized(new { error = "invalid_credentials" });

        var device = await db.Devices.SingleOrDefaultAsync(x =>
            x.ClientId == credential.ClientId && x.ExternalDeviceId == request.DeviceId);
        if (device?.Status == "Disabled" || device?.Status == "Deleted")
            return StatusCode(403, new { error = "device_disabled" });

        if (device is null && !await db.DeviceRegistrationRequests.AnyAsync(x =>
            x.ClientId == credential.ClientId && x.ExternalDeviceId == request.DeviceId && x.Status == "Pending"))
        {
            db.DeviceRegistrationRequests.Add(new DeviceRegistrationRequest
            {
                ClientId = credential.ClientId, ExternalDeviceId = request.DeviceId,
                RequestedName = request.DeviceName ?? request.DeviceId,
                Status = "Pending", RequestedAtUtc = DateTime.UtcNow
            });
        }

        var pair = CreateSession(credential.ClientId, device?.Id, request.DeviceId, null);
        await db.SaveChangesAsync();
        return Ok(new { pair.AccessToken, pair.RefreshToken, expiresInSeconds = 900,
            deviceRegistrationRequired = device is null });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest request)
    {
        var hash = TokenHelper.Hash(request.RefreshToken);
        var current = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash);
        if (current is null || current.RevokedAtUtc is not null || current.ExpiresAtUtc <= DateTime.UtcNow)
            return Unauthorized(new { error = "invalid_refresh_token" });
        var client = await db.Clients.FindAsync(current.ClientId);
        if (client is null || !client.IsActive) return StatusCode(403, new { error = "client_disabled" });

        current.RevokedAtUtc = DateTime.UtcNow;
        current.RevokeReason = "rotated";
        var pair = CreateSession(current.ClientId, current.DeviceId,
            current.RequestedExternalDeviceId, current.TokenFamilyId);
        await db.SaveChangesAsync();
        current.ReplacedByTokenId = pair.Session.Id;
        await db.SaveChangesAsync();
        return Ok(new { pair.AccessToken, pair.RefreshToken, expiresInSeconds = 900 });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var sessionId = HttpContext.Items["SessionId"] as int?;
        if (sessionId is null) return Unauthorized();
        var session = await db.RefreshTokens.FindAsync(sessionId.Value);
        if (session is not null && session.RevokedAtUtc is null)
        {
            session.RevokedAtUtc = DateTime.UtcNow;
            session.RevokeReason = "logout";
            await db.SaveChangesAsync();
        }
        return NoContent();
    }

    private SessionPair CreateSession(int clientId, int? deviceId, string externalDeviceId, string? familyId)
    {
        var access = TokenHelper.Create(32);
        var refresh = TokenHelper.Create(48);
        var session = new RefreshToken
        {
            ClientId = clientId, DeviceId = deviceId, RequestedExternalDeviceId = externalDeviceId,
            AccessTokenHash = TokenHelper.Hash(access), AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
            TokenHash = TokenHelper.Hash(refresh), TokenFamilyId = familyId ?? Guid.NewGuid().ToString(),
            CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddDays(30)
        };
        db.RefreshTokens.Add(session);
        return new SessionPair(access, refresh, session);
    }
}

public record LoginRequest(string Login, string Password, string DeviceId, string? DeviceName = null);
public record RefreshRequest(string RefreshToken);
public record SessionPair(string AccessToken, string RefreshToken, RefreshToken Session);
