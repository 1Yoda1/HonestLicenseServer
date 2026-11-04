using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/connection-requests")]
public sealed class ConnectionRequestsController(
    HonestDbContext db,
    IOptionsSnapshot<ConnectionRequestPrivacyOptions> privacyOptions,
    ILogger<ConnectionRequestsController> logger) : ControllerBase
{
    [HttpGet("policy")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Policy()
    {
        var privacy = privacyOptions.Value;
        if (!privacy.IsReady) return Ok(new { available = false });
        return Ok(new
        {
            available = true,
            consentRequired = privacy.ConsentRequired,
            privacyPolicyVersion = privacy.PrivacyPolicyVersion,
            privacyPolicyText = privacy.PrivacyPolicyText,
            consentVersion = privacy.ConsentRequired ? privacy.ConsentVersion : null,
            consentText = privacy.ConsentRequired ? privacy.ConsentText : null
        });
    }

    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("connection-requests")]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<ConnectionRequestCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ConnectionRequestErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ConnectionRequestErrorResponse>(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ConnectionRequestCreatedResponse>> Create(
        CreateConnectionRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Website))
            return NoContent();

        var privacy = privacyOptions.Value;
        if (!privacy.IsReady)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ConnectionRequestErrorResponse(false, "Приём заявок через сайт временно недоступен."));
        if (request.PrivacyPolicyVersion != privacy.PrivacyPolicyVersion ||
            (privacy.ConsentRequired && request.ConsentVersion != privacy.ConsentVersion))
            return Conflict(new ConnectionRequestErrorResponse(false,
                "Документы обновились. Ознакомьтесь с ними и отправьте заявку повторно."));
        if (privacy.ConsentRequired && !request.Consent)
            return BadRequest(new ConnectionRequestErrorResponse(false,
                "Для отправки заявки необходимо отдельное согласие на обработку персональных данных."));

        var item = new ConnectionRequest
        {
            CreatedAtUtc = DateTime.UtcNow,
            StatusChangedAtUtc = DateTime.UtcNow,
            ProcessingLegalBasis = privacy.LegalBasis,
            PrivacyPolicyVersion = privacy.PrivacyPolicyVersion,
            ConsentVersion = privacy.ConsentRequired ? privacy.ConsentVersion : null,
            ConsentText = privacy.ConsentRequired ? privacy.ConsentText : null,
            ConsentReceivedAtUtc = privacy.ConsentRequired ? DateTime.UtcNow : null,
            ContactName = request.ContactName.Trim(),
            Company = Clean(request.Company),
            Phone = request.Phone.Trim(),
            Email = Clean(request.Email),
            City = Clean(request.City),
            WorkplaceCount = request.WorkplaceCount,
            InventorySystem = Clean(request.InventorySystem),
            Comment = Clean(request.Comment),
            Source = Clean(request.Source),
            Status = "New",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(Request.Headers.UserAgent.ToString(), 1000)
        };

        try
        {
            db.ConnectionRequests.Add(item);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(exception, "Could not save connection request");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new ConnectionRequestErrorResponse(false, "Не удалось отправить заявку. Попробуйте позже."));
        }

        return StatusCode(StatusCodes.Status201Created,
            new ConnectionRequestCreatedResponse(true, item.Id));
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, length)];
}
