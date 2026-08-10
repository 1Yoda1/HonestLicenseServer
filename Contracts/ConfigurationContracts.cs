using System.ComponentModel.DataAnnotations;

namespace HonestLicenseServer.Contracts;

public sealed record RegistrationStatusResponse(
    string DeviceId, string Status, DateTime RequestedAtUtc,
    DateTime? ResolvedAtUtc, string? Comment);

public sealed record ConfigurationResponse(
    DateTime ConfigurationRevision,
    ClientConfiguration Client,
    DeviceConfiguration Device,
    LicensePolicyConfiguration? LicensePolicy,
    IReadOnlyList<ComponentConfiguration> Components);

public sealed record ClientConfiguration(
    string ClientId, string Name, string? Architecture,
    bool HasLmDatabaseBackup, bool RuDesktopEnabled,
    bool RuDesktopAutoOfferPasswordSetup,
    string? IdentificationCode, string? ChzToken);

public sealed record ClientIntegrationSettingsResponse(
    string ClientId, string? IdentificationCode, string? ChzToken,
    bool IsConfigured);

public sealed record PutClientIntegrationSettingsRequest(
    [Required, StringLength(256, MinimumLength = 1)] string IdentificationCode,
    [Required, StringLength(2048, MinimumLength = 1)] string ChzToken);

public sealed record DeviceConfiguration(
    string DeviceId, string Name, string? Address, string Status);

public sealed record LicensePolicyConfiguration(
    bool IsEnabled, string? MinimumHonestFlowVersion,
    int OfflineGraceHours, long SourceRevision, DateTime SourceValidUntilUtc);

public sealed record ComponentConfiguration(
    string Component, string GlobalVersion, string? OverrideVersion,
    string EffectiveVersion, string? FileName, string? DownloadUrl,
    string? Sha256, long? SizeBytes, bool IsOverride);

public sealed record PutComponentAssetRequest(
    [Required, StringLength(260, MinimumLength = 1)] string FileName,
    [Required, Url, StringLength(2048)] string DownloadUrl,
    [RegularExpression("^[A-Fa-f0-9]{64}$")] string? Sha256,
    [Range(0, long.MaxValue)] long? SizeBytes);

public sealed record PutComponentOverrideRequest(
    [StringLength(100)] string? RequiredVersion);
