using System.ComponentModel.DataAnnotations;

namespace HonestLicenseServer.Contracts;

public sealed record LoginRequest(
    [Required, StringLength(256, MinimumLength = 1)] string Password,
    [Required, StringLength(128, MinimumLength = 1)] string DeviceId);

public sealed record RefreshRequest(
    [Required, StringLength(512, MinimumLength = 1)] string RefreshToken);

public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    bool DeviceRegistrationRequired);

public sealed record DeviceRegistrationRequestDto(
    [Required, StringLength(128, MinimumLength = 1)] string DeviceId,
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Required, StringLength(300, MinimumLength = 1), RegularExpression(@".*\S.*")]
    string Address);

public sealed record DeviceRegistrationResponse(int Id, string Status, DateTime RequestedAtUtc);

public sealed record LicenseResponse(
    string GrantBase64,
    string SignatureBase64,
    string KeyId,
    long Revision,
    DateTime IssuedAtUtc,
    DateTime ValidUntilUtc);

public sealed record VersionResponse(string Application, string CurrentVersion, DateTime ImportedAtUtc);
