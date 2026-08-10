namespace HonestLicenseServer.Models;

public class Client
{
    public int Id { get; set; }
    public required string ExternalClientId { get; set; }
    public required string Name { get; set; }
    public string? Inn { get; set; }
    public string? Architecture { get; set; }
    public bool IsActive { get; set; }
    public bool HasLmDatabaseBackup { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<Credential> Credentials { get; set; } = [];
    public List<Device> Devices { get; set; } = [];
    public List<License> Licenses { get; set; } = [];
    public ClientSetting? Settings { get; set; }
}

public class Credential
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public required string Login { get; set; }
    public required string PasswordHash { get; set; }
    public string? LegacyTokenHash { get; set; }
    public bool IsActive { get; set; }
    public DateTime PasswordChangedAtUtc { get; set; }
    public Client Client { get; set; } = null!;
}

public class Device
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public required string ExternalDeviceId { get; set; }
    public required string Name { get; set; }
    public string? Address { get; set; }
    public string? Comment { get; set; }
    public required string Status { get; set; }
    public DateTime RegisteredAtUtc { get; set; }
    public Client Client { get; set; } = null!;
    public List<License> Licenses { get; set; } = [];
}

public class License
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public int DeviceId { get; set; }
    public long Revision { get; set; }
    public required string GrantJson { get; set; }
    public required byte[] GrantBytes { get; set; }
    public required string SignatureBase64 { get; set; }
    public required string KeyId { get; set; }
    public required string SignatureScope { get; set; }
    public DateTime? SignatureVerifiedAtUtc { get; set; }
    public required string Status { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ValidUntilUtc { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public Client Client { get; set; } = null!;
    public Device Device { get; set; } = null!;
}

public class AppVersion
{
    public int Id { get; set; }
    public required string Application { get; set; }
    public required string CurrentVersion { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

public class ClientComponentVersion
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public required string Component { get; set; }
    public string? RequiredVersion { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public Client Client { get; set; } = null!;
}

public class ComponentAsset
{
    public int Id { get; set; }
    public required string Component { get; set; }
    public required string Version { get; set; }
    public required string Architecture { get; set; }
    public required string FileName { get; set; }
    public string? DownloadUrl { get; set; }
    public string? YandexPublicKey { get; set; }
    public string? YandexPath { get; set; }
    public string? Sha256 { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class ClientSetting
{
    public int ClientId { get; set; }
    public string? IdentificationCode { get; set; }
    public string? ChzToken { get; set; }
    public bool RuDesktopEnabled { get; set; }
    public bool RuDesktopAutoOfferPasswordSetup { get; set; }
    public string? RuDesktopPasswordHash { get; set; }
    public string? EngineerAlgorithm { get; set; }
    public int? EngineerIterations { get; set; }
    public string? EngineerSaltBase64 { get; set; }
    public string? EngineerPasswordHashBase64 { get; set; }
    public Client Client { get; set; } = null!;
}

public class LicensePolicy
{
    public int ClientId { get; set; }
    public bool IsEnabled { get; set; }
    public string? MinimumHonestFlowVersion { get; set; }
    public int OfflineGraceHours { get; set; }
    public long SourceRevision { get; set; }
    public DateTime SourceIssuedAtUtc { get; set; }
    public DateTime SourceValidUntilUtc { get; set; }
    public Client Client { get; set; } = null!;
}

public class DeviceRegistrationRequest
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public required string ExternalDeviceId { get; set; }
    public required string RequestedName { get; set; }
    public string? RequestedAddress { get; set; }
    public string? RequestedHonestFlowVersion { get; set; }
    public required string Status { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? Comment { get; set; }
    public Client Client { get; set; } = null!;
}

public class RefreshToken
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public int? DeviceId { get; set; }
    public required string RequestedExternalDeviceId { get; set; }
    public required string AccessTokenHash { get; set; }
    public DateTime AccessTokenExpiresAtUtc { get; set; }
    public required string TokenHash { get; set; }
    public required string TokenFamilyId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public int? ReplacedByTokenId { get; set; }
    public string? RevokeReason { get; set; }
    public Client Client { get; set; } = null!;
    public Device? Device { get; set; }
}

public class AuditEvent
{
    public int Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public int? ClientId { get; set; }
    public string? DetailsJson { get; set; }
    public string? IpAddress { get; set; }
    public required string CorrelationId { get; set; }
}

public class SupportRequest
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public int? DeviceId { get; set; }
    public required string ExternalDeviceId { get; set; }
    public required string Subject { get; set; }
    public required string Message { get; set; }
    public required string Contact { get; set; }
    public string? HonestFlowVersion { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public Client Client { get; set; } = null!;
    public Device? Device { get; set; }
}

public class ConnectionRequest
{
    public int Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public required string ContactName { get; set; }
    public string? Company { get; set; }
    public required string Phone { get; set; }
    public string? Email { get; set; }
    public string? City { get; set; }
    public int WorkplaceCount { get; set; }
    public string? InventorySystem { get; set; }
    public string? Comment { get; set; }
    public string? Source { get; set; }
    public required string Status { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime? NotificationSentAtUtc { get; set; }
    public string? NotificationError { get; set; }
}
