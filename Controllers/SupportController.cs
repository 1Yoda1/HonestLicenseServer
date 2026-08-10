using HonestLicenseServer.Authentication;
using HonestLicenseServer.Contracts;
using HonestLicenseServer.Data;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HonestLicenseServer.Controllers;

[ApiController]
[Route("api/support")]
public sealed class SupportController(HonestDbContext db) : ControllerBase
{
    [HttpPost("requests")]
    [Authorize(Policy = OpaqueBearerDefaults.ActiveClientPolicy)]
    [EnableRateLimiting("support")]
    [ProducesResponseType<SupportRequestAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SupportRequestAcceptedResponse>> Create(CreateSupportRequest request)
    {
        var item = new SupportRequest
        {
            ClientId = User.ClientId(), DeviceId = User.DeviceId(),
            ExternalDeviceId = User.ExternalDeviceId(), Subject = request.Subject.Trim(),
            Message = request.Message.Trim(), Contact = request.Contact.Trim(),
            HonestFlowVersion = request.HonestFlowVersion?.Trim(), Status = "Accepted",
            CreatedAtUtc = DateTime.UtcNow
        };
        db.SupportRequests.Add(item);
        await db.SaveChangesAsync();
        return Accepted(new SupportRequestAcceptedResponse(item.Id, item.Status, item.CreatedAtUtc));
    }
}
