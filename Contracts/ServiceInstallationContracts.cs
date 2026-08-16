using System.ComponentModel.DataAnnotations;

namespace HonestLicenseServer.Contracts;

public sealed record ServiceInstallAccessRequest(
    [Required, StringLength(512, MinimumLength = 1)] string Password,
    [StringLength(64)] string? AppVersion,
    [StringLength(20)] string? Architecture);

public sealed record ServiceInstallAccessResponse(
    string AccessToken,
    int ExpiresInSeconds,
    string Scope,
    IReadOnlyList<ServiceInstallComponentResponse> Components);

public sealed record ServiceInstallComponentResponse(
    string Component,
    string Version,
    string? FileName,
    string? DownloadUrl,
    string? Sha256,
    long? SizeBytes,
    string? Architecture);

public sealed record ServiceInstallAccessSettingsResponse(
    bool IsEnabled,
    bool HasPassword,
    DateTime? UpdatedAtUtc);

public sealed record PutServiceInstallAccessSettingsRequest(
    bool IsEnabled,
    [StringLength(512, MinimumLength = 10)] string? NewPassword);
