using System.ComponentModel.DataAnnotations;

namespace HonestLicenseServer.Contracts;

public sealed record CreateSupportRequest(
    [Required, StringLength(200, MinimumLength = 3)] string Subject,
    [Required, StringLength(5000, MinimumLength = 3)] string Message,
    [Required, StringLength(300, MinimumLength = 3)] string Contact,
    [StringLength(100)] string? HonestFlowVersion);

public sealed record SupportRequestAcceptedResponse(int Id, string Status, DateTime CreatedAtUtc);

public sealed record CreateDiagnosticUploadRequest(
    [Required, StringLength(240, MinimumLength = 5)] string FileName,
    [Range(1, 524_288_000)] long SizeBytes,
    [Required, RegularExpression("^[A-Fa-f0-9]{64}$")] string Sha256,
    [StringLength(100)] string? HonestFlowVersion);

public sealed record DiagnosticUploadResponse(
    int Id,
    string UploadUrl,
    DateTime UploadUrlExpiresAtUtc,
    DateTime ArchiveExpiresAtUtc);

public sealed record CompleteDiagnosticUploadRequest(
    [Range(1, 524_288_000)] long SizeBytes,
    [Required, RegularExpression("^[A-Fa-f0-9]{64}$")] string Sha256);

public sealed record AdminDiagnosticArchiveResponse(
    int Id,
    string ClientId,
    string ClientName,
    string DeviceId,
    string DeviceName,
    string Address,
    string FileName,
    long SizeBytes,
    string Sha256,
    string? HonestFlowVersion,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? UploadedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? LastDownloadedAtUtc,
    DateTime? DeletedAtUtc);

public sealed record DiagnosticDownloadResponse(string DownloadUrl, DateTime ExpiresAtUtc);
