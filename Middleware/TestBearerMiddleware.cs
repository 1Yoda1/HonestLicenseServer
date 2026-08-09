using HonestLicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Middleware;

public class TestBearerMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, HonestDbContext db)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var hash = TokenHelper.Hash(header["Bearer ".Length..].Trim());
            var session = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(x =>
                x.AccessTokenHash == hash && x.RevokedAtUtc == null &&
                x.AccessTokenExpiresAtUtc > DateTime.UtcNow);
            if (session is not null)
            {
                context.Items["ClientId"] = session.ClientId;
                context.Items["DeviceId"] = session.DeviceId;
                context.Items["ExternalDeviceId"] = session.RequestedExternalDeviceId;
                context.Items["SessionId"] = session.Id;
            }
        }
        await next(context);
    }
}
