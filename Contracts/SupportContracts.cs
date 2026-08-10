using System.ComponentModel.DataAnnotations;

namespace HonestLicenseServer.Contracts;

public sealed record CreateSupportRequest(
    [Required, StringLength(200, MinimumLength = 3)] string Subject,
    [Required, StringLength(5000, MinimumLength = 3)] string Message,
    [Required, StringLength(300, MinimumLength = 3)] string Contact,
    [StringLength(100)] string? HonestFlowVersion);

public sealed record SupportRequestAcceptedResponse(int Id, string Status, DateTime CreatedAtUtc);
