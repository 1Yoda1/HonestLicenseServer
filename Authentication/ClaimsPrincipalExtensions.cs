using System.Security.Claims;

namespace HonestLicenseServer.Authentication;

public static class ClaimsPrincipalExtensions
{
    public static int ClientId(this ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirstValue(HonestClaimTypes.ClientId)!);

    public static int? DeviceId(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(HonestClaimTypes.DeviceId), out var value) ? value : null;

    public static int SessionId(this ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirstValue(HonestClaimTypes.SessionId)!);

    public static string ExternalDeviceId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(HonestClaimTypes.ExternalDeviceId)!;
}
