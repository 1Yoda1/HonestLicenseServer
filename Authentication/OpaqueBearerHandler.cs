using System.Security.Claims;
using System.Text.Encodings.Web;
using HonestLicenseServer.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HonestLicenseServer.Authentication;

public sealed class OpaqueBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    HonestDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = authorization["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
            return AuthenticateResult.Fail("The bearer token is empty.");

        var tokenHash = TokenHelper.Hash(token);
        var session = await db.RefreshTokens
            .AsNoTracking()
            .Include(x => x.Client)
            .Include(x => x.Device)
            .SingleOrDefaultAsync(x => x.AccessTokenHash == tokenHash, Context.RequestAborted);

        if (session is null || session.RevokedAtUtc is not null ||
            session.AccessTokenExpiresAtUtc <= DateTime.UtcNow)
            return AuthenticateResult.Fail("The bearer token is invalid or expired.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.ClientId.ToString()),
            new(HonestClaimTypes.ClientId, session.ClientId.ToString()),
            new(HonestClaimTypes.ExternalClientId, session.Client.ExternalClientId),
            new(HonestClaimTypes.ClientActive, session.Client.IsActive.ToString()),
            new(HonestClaimTypes.ExternalDeviceId, session.RequestedExternalDeviceId),
            new(HonestClaimTypes.SessionId, session.Id.ToString())
        };

        if (session.Device is not null)
        {
            claims.Add(new(HonestClaimTypes.DeviceId, session.Device.Id.ToString()));
            claims.Add(new(HonestClaimTypes.DeviceStatus, session.Device.Status));
        }

        var identity = new ClaimsIdentity(claims, OpaqueBearerDefaults.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity), OpaqueBearerDefaults.Scheme));
    }
}
