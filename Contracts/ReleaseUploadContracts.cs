using System.ComponentModel.DataAnnotations;

namespace HonestLicenseServer.Contracts;

public sealed record CreateReleaseUploadRequest(
    [Required, StringLength(100)] string KeyId,
    [Required, StringLength(24000)] string ManifestBase64,
    [Required, StringLength(1024)] string SignatureBase64,
    bool UseMultipart = false);

public sealed record ReleaseUploadResponse(
    int Id, string Component, string Version, string Architecture, string FileName,
    long SizeBytes, string Sha256, string? UploadUrl, DateTime UploadUrlExpiresAtUtc,
    long? PartSizeBytes = null, int? PartCount = null);

public sealed record ReleaseUploadPartResponse(
    int PartNumber, string UploadUrl, DateTime UploadUrlExpiresAtUtc);

public sealed record CompleteReleaseUploadRequest(
    [Required, MinLength(1)] IReadOnlyList<CompletedReleaseUploadPart> Parts);

public sealed record CompletedReleaseUploadPart(
    [Range(1, 10_000)] int PartNumber,
    [Required, StringLength(200, MinimumLength = 1)] string ETag);
