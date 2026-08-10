using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace HonestLicenseServer.Authentication;

public sealed class HonestAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            await ApiProblems.WriteAsync(context, StatusCodes.Status401Unauthorized,
                "invalid_access_token", "Authentication is required",
                "A valid Bearer access token is required.");
            return;
        }

        if (authorizeResult.Forbidden)
        {
            if (!string.Equals(context.User.FindFirst(HonestClaimTypes.ClientActive)?.Value,
                    bool.TrueString, StringComparison.OrdinalIgnoreCase))
            {
                await ApiProblems.WriteAsync(context, StatusCodes.Status403Forbidden,
                    "client_disabled", "Client is disabled");
                return;
            }

            var deviceStatus = context.User.FindFirst(HonestClaimTypes.DeviceStatus)?.Value;
            if (deviceStatus is null)
            {
                await ApiProblems.WriteAsync(context, StatusCodes.Status403Forbidden,
                    "device_pending", "Device confirmation is required",
                    "The device is awaiting administrator approval.");
                return;
            }

            await ApiProblems.WriteAsync(context, StatusCodes.Status403Forbidden,
                "device_disabled", "Device is disabled");
            return;
        }

        await _fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
