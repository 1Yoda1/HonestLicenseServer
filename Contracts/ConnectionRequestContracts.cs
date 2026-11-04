using System.ComponentModel.DataAnnotations;

namespace HonestLicenseServer.Contracts;

public sealed record CreateConnectionRequest(
    [Required, StringLength(100, MinimumLength = 1)] string ContactName,
    [StringLength(150)] string? Company,
    [Required, StringLength(40), ConnectionPhone] string Phone,
    [EmailAddress, StringLength(150)] string? Email,
    [Required, StringLength(100, MinimumLength = 1)] string? City,
    [Range(1, 100000)] int WorkplaceCount,
    [StringLength(150)] string? InventorySystem,
    [StringLength(2000)] string? Comment,
    [StringLength(100)] string? Source,
    [StringLength(200)] string? Website,
    bool Consent = false,
    [StringLength(100)] string? ConsentVersion = null,
    [StringLength(100)] string? PrivacyPolicyVersion = null);

public sealed record ConnectionRequestCreatedResponse(bool Success, int RequestId);

public sealed record ConnectionRequestErrorResponse(bool Success, string Message);

public sealed class ConnectionPhoneAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if (value is not string raw) return false;
        var phone = raw.Trim();
        if (phone.Length > 40 ||
            !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^\+?[0-9][0-9 ()-]*[0-9]$"))
            return false;

        var digits = phone.Count(char.IsDigit);
        if (digits is < 10 or > 15) return false;
        if (phone.StartsWith('+')) return phone[1] != '0' && digits >= 10;
        return digits == 11 && (phone[0] is '7' or '8');
    }
}

public sealed record UpdateConnectionRequestStatus(
    [Required, RegularExpression("^(New|InProgress|Completed|Rejected)$")] string Status,
    [Required, RegularExpression("^(New|InProgress|Completed|Rejected)$")] string ExpectedStatus);

public sealed record AdminConnectionRequestResponse(
    int Id, DateTime CreatedAtUtc, string ContactName, string? Company, string Phone,
    string? Email, string? City, int WorkplaceCount, string? InventorySystem,
    string? Comment, string? Source, string Status);
