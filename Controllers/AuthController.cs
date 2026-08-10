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
[Route("api/auth")]
public class AuthController(HonestDbContext db, LoginAttemptLimiter loginLimiter) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<TokenResponse>> Login(LoginRequest request)
    {
        if (!loginLimiter.TryAcquire(request.Login, DateTime.UtcNow))
            return ApiProblems.Create(HttpContext, StatusCodes.Status429TooManyRequests,
                "rate_limit_exceeded", "Too many requests", "Try again later.");

        var credential = await db.Credentials.Include(x => x.Client)
            .SingleOrDefaultAsync(x => x.Login == request.Login && x.IsActive);
        if (credential is null || !PasswordHasher.Verify(request.Password, credential.PasswordHash))
            return ApiProblems.Create(HttpContext, StatusCodes.Status401Unauthorized,
                "invalid_credentials", "Invalid credentials");
        if (!credential.Client.IsActive)
            return ApiProblems.Create(HttpContext, StatusCodes.Status403Forbidden,
                "client_disabled", "Client is disabled");

        loginLimiter.Reset(request.Login);

        var device = await db.Devices.SingleOrDefaultAsync(x =>
            x.ClientId == credential.ClientId && x.ExternalDeviceId == request.DeviceId);
        if (device?.Status == "Disabled" || device?.Status == "Deleted")
            return ApiProblems.Create(HttpContext, StatusCodes.Status403Forbidden,
                "device_disabled", "Device is disabled");

        if (device is null && !await db.DeviceRegistrationRequests.AnyAsync(x =>
            x.ClientId == credential.ClientId && x.ExternalDeviceId == request.DeviceId))
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
        return Ok(new TokenResponse(pair.AccessToken, pair.RefreshToken, 900, device is null));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("refresh")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TokenResponse>> Refresh(RefreshRequest request)
    {
        var hash = TokenHelper.Hash(request.RefreshToken);
        var current = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash);
        if (current is null || current.RevokedAtUtc is not null || current.ExpiresAtUtc <= DateTime.UtcNow)
            return ApiProblems.Create(HttpContext, StatusCodes.Status401Unauthorized,
                "invalid_refresh_token", "Invalid refresh token");
        var client = await db.Clients.FindAsync(current.ClientId);
        if (client is null || !client.IsActive)
            return ApiProblems.Create(HttpContext, StatusCodes.Status403Forbidden,
                "client_disabled", "Client is disabled");
        if (current.DeviceId is not null)
        {
            var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == current.DeviceId);
            if (device?.Status != "Active")
                return ApiProblems.Create(HttpContext, StatusCodes.Status403Forbidden,
                    "device_disabled", "Device is disabled");
        }

        current.RevokedAtUtc = DateTime.UtcNow;
        current.RevokeReason = "rotated";
        var pair = CreateSession(current.ClientId, current.DeviceId,
            current.RequestedExternalDeviceId, current.TokenFamilyId);
        await db.SaveChangesAsync();
        current.ReplacedByTokenId = pair.Session.Id;
        await db.SaveChangesAsync();
        return Ok(new TokenResponse(pair.AccessToken, pair.RefreshToken, 900, pair.Session.DeviceId is null));
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout()
    {
        var session = await db.RefreshTokens.FindAsync(User.SessionId());
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
public record SessionPair(string AccessToken, string RefreshToken, RefreshToken Session);
