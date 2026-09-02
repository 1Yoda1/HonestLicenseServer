using System.Text.Json;
using HonestLicenseServer.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Authentication;

public sealed class ServiceEntitlementRequirement : IAuthorizationRequirement
{
    public const string FailureReason = "service_required";
}

public sealed class ServiceEntitlementHandler(HonestDbContext db)
    : AuthorizationHandler<ServiceEntitlementRequirement>
{
    private static readonly JsonSerializerOptions GrantJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ServiceEntitlementRequirement requirement)
    {
        if (!int.TryParse(context.User.FindFirst(HonestClaimTypes.ClientId)?.Value, out int clientId) ||
            !int.TryParse(context.User.FindFirst(HonestClaimTypes.DeviceId)?.Value, out int deviceId))
        {
            Deny(context);
            return;
        }

        var cancellationToken = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        bool policyEnabled = await db.LicensePolicies.AsNoTracking()
            .AnyAsync(x => x.ClientId == clientId && x.IsEnabled, cancellationToken);
        if (!policyEnabled)
        {
            Deny(context);
            return;
        }

        // Select the newest verified personal grant before examining its status. A revoked,
        // expired or otherwise ineligible newer grant must never fall back to older history.
        var license = await db.Licenses.AsNoTracking()
            .Where(x => x.ClientId == clientId && x.DeviceId == deviceId &&
                x.SignatureScope == "PersonalGrant" && x.SignatureVerifiedAtUtc != null)
            .OrderByDescending(x => x.Revision)
            .Select(x => new { x.Status, x.GrantBytes })
            .FirstOrDefaultAsync(cancellationToken);
        if (license is null || !string.Equals(license.Status, "Active", StringComparison.Ordinal))
        {
            Deny(context);
            return;
        }

        SignedPersonalGrant? grant;
        try
        {
            grant = JsonSerializer.Deserialize<SignedPersonalGrant>(license.GrantBytes, GrantJsonOptions);
        }
        catch (JsonException)
        {
            Deny(context);
            return;
        }

        if (grant is null || grant.ValidUntilUtc <= DateTime.UtcNow ||
            !string.Equals(grant.ClientId,
                context.User.FindFirst(HonestClaimTypes.ExternalClientId)?.Value,
                StringComparison.Ordinal) ||
            !string.Equals(grant.DeviceId,
                context.User.FindFirst(HonestClaimTypes.ExternalDeviceId)?.Value,
                StringComparison.Ordinal) ||
            grant.ClientEnabled != true || grant.DeviceEnabled != true || grant.OperatorDevice ||
            grant.Features is null || !grant.Features.Contains("Service", StringComparer.Ordinal))
        {
            Deny(context);
            return;
        }

        context.Succeed(requirement);
    }

    private void Deny(AuthorizationHandlerContext context) =>
        context.Fail(new AuthorizationFailureReason(this,
            ServiceEntitlementRequirement.FailureReason));

    private sealed class SignedPersonalGrant
    {
        public string? ClientId { get; init; }
        public string? DeviceId { get; init; }
        public DateTime ValidUntilUtc { get; init; }
        public bool? ClientEnabled { get; init; }
        public bool? DeviceEnabled { get; init; }
        public bool OperatorDevice { get; init; }
        public string[]? Features { get; init; }
    }
}
