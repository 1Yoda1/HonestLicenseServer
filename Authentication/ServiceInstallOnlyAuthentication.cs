using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using HonestLicenseServer.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace HonestLicenseServer.Authentication;

public static class ServiceInstallOnlyDefaults
{
    public const string Scheme = "ServiceInstallOnly";
    public const string Policy = "ServiceInstallOnly";
    public const string Scope = "installation_only";
    public const string ScopeClaim = "honest:scope";
    public const string ArchitectureClaim = "honest:install_architecture";
}

public sealed record ServiceInstallToken(string Value, DateTime ExpiresAtUtc);

public sealed class ServiceInstallTokenStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public ServiceInstallToken Issue(string architecture)
    {
        RemoveExpired();
        string value = "hfi_" + TokenHelper.Create(32);
        DateTime expiresAtUtc = DateTime.UtcNow.Add(Lifetime);
        _sessions[TokenHelper.Hash(value)] = new Session(expiresAtUtc, architecture);
        return new ServiceInstallToken(value, expiresAtUtc);
    }

    public bool TryValidate(string value, out string architecture)
    {
        architecture = "any";
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("hfi_", StringComparison.Ordinal))
            return false;
        string hash = TokenHelper.Hash(value);
        if (!_sessions.TryGetValue(hash, out Session? session)) return false;
        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _sessions.TryRemove(hash, out _);
            return false;
        }
        architecture = session.Architecture;
        return true;
    }

    public void RevokeAll() => _sessions.Clear();

    private void RemoveExpired()
    {
        DateTime now = DateTime.UtcNow;
        foreach ((string hash, Session session) in _sessions)
            if (session.ExpiresAtUtc <= now) _sessions.TryRemove(hash, out _);
    }

    private sealed record Session(DateTime ExpiresAtUtc, string Architecture);
}

public sealed class ServiceInstallOnlyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ServiceInstallTokenStore tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        string token = authorization["Bearer ".Length..].Trim();
        if (!tokens.TryValidate(token, out string architecture))
            return Task.FromResult(AuthenticateResult.Fail("The install-only token is invalid or expired."));

        Claim[] claims =
        [
            new(ServiceInstallOnlyDefaults.ScopeClaim, ServiceInstallOnlyDefaults.Scope),
            new(ServiceInstallOnlyDefaults.ArchitectureClaim, architecture)
        ];
        var identity = new ClaimsIdentity(claims, ServiceInstallOnlyDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity), ServiceInstallOnlyDefaults.Scheme)));
    }
}
