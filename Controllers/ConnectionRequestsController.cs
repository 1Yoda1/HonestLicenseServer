using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/connection-requests")]
public sealed class ConnectionRequestsController(
    HonestDbContext db,
    IConnectionRequestNotifier notifier,
    ILogger<ConnectionRequestsController> logger) : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("connection-requests")]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<ConnectionRequestCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ConnectionRequestErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ConnectionRequestErrorResponse>(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ConnectionRequestCreatedResponse>> Create(
        CreateConnectionRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Website))
            return NoContent();

        var item = new ConnectionRequest
        {
            CreatedAtUtc = DateTime.UtcNow,
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

        try
        {
            await notifier.NotifyAsync(item, cancellationToken);
            item.NotificationSentAtUtc = DateTime.UtcNow;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            item.NotificationError = Truncate(exception.Message, 2000);
            logger.LogError(exception, "Connection request {RequestId} saved, but notification failed", item.Id);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(exception, "Could not update notification state for connection request {RequestId}", item.Id);
        }

        return StatusCode(StatusCodes.Status201Created,
            new ConnectionRequestCreatedResponse(true, item.Id));
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, length)];
}
