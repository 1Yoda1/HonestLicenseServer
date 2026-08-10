using System.ComponentModel.DataAnnotations;

namespace HonestLicenseServer.Contracts;

public sealed record CreateConnectionRequest(
    [Required, StringLength(100, MinimumLength = 1)] string ContactName,
    [StringLength(150)] string? Company,
    [Required, StringLength(40, MinimumLength = 3)] string Phone,
    [EmailAddress, StringLength(150)] string? Email,
    [StringLength(100)] string? City,
    [Range(1, 100000)] int WorkplaceCount,
    [StringLength(150)] string? InventorySystem,
    [StringLength(2000)] string? Comment,
    [StringLength(100)] string? Source,
    [StringLength(200)] string? Website);

public sealed record ConnectionRequestCreatedResponse(bool Success, int RequestId);

public sealed record ConnectionRequestErrorResponse(bool Success, string Message);
