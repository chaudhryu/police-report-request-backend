using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using police_report_request_backend.Contracts.Requests;
using police_report_request_backend.Data;

namespace police_report_request_backend.Controllers;

[ApiController]
[Route("api/submitted-requests")]
[Authorize]
public sealed class SubmittedRequestsController : ControllerBase
{
    private readonly SubmittedRequestFormRepository _formsRepo;

    public SubmittedRequestsController(SubmittedRequestFormRepository formsRepo) => _formsRepo = formsRepo;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SubmitRequestFormRequest req)
    {
        var badge = TryGetBadgeFromClaims(User);
        if (string.IsNullOrWhiteSpace(badge))
            return Forbid("Missing 'badge' claim.");

        var id = await _formsRepo.InsertAsync(req.RequestFormId, badge!, req.SubmittedRequestData);
        return Created($"/api/submitted-requests/{id}", new { id });
    }

    private static string? TryGetBadgeFromClaims(ClaimsPrincipal user) =>
        user.FindFirst("badge")?.Value
        ?? user.FindFirst("employee_badge")?.Value
        ?? user.FindFirst("employeeId")?.Value
        ?? user.FindFirst("employeeid")?.Value;
}
