using System.ComponentModel.DataAnnotations;

namespace HonestLicenseServer.Contracts;

public sealed record RemoteAccessKeyResponse(string KeyId, string PublicKeyBase64,
    string SignatureBase64, string SigningKeyId);

public sealed record PublishRemoteAccessKeyRequest(
    [Required, StringLength(100)] string KeyId,
    [Required] string PublicKeyBase64,
    [Required] string SignatureBase64,
    [Required, StringLength(100)] string SigningKeyId);

public sealed record PutRemoteAccessCredentialRequest(
    [Required, StringLength(100)] string CredentialId,
    [Required, StringLength(100)] string KeyId,
    [Required, StringLength(100)] string RuDesktopId,
    [Required] string CiphertextBase64);

public sealed record RemoteAccessCredentialStatusResponse(
    bool IsActive, string? CredentialId, string? RuDesktopId, string? KeyId,
    int? ActiveRequestId, DateTime? ActiveRequestCreatedAtUtc);

public sealed record CreateRemoteSupportRequest(
    [Required, StringLength(200, MinimumLength = 3)] string Subject,
    [Required, StringLength(5000, MinimumLength = 3)] string Message,
    [Required, StringLength(300, MinimumLength = 3)] string Contact,
    [StringLength(100)] string? HonestFlowVersion,
    [Required, StringLength(100)] string CredentialId);

public sealed record AdminRemoteAccessCredentialResponse(
    string CredentialId, string KeyId, string RuDesktopId, string CiphertextBase64);
