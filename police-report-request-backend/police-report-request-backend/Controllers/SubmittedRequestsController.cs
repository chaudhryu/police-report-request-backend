using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using police_report_request_backend.Contracts.Requests;
using police_report_request_backend.Data;
using System.Globalization; // at top for date parsing

namespace police_report_request_backend.Controllers;

[ApiController]
[Route("api/submitted-requests")]
[Authorize]
public sealed class SubmittedRequestsController : ControllerBase
{
    private readonly SubmittedRequestFormRepository _formsRepo;
    private readonly UsersRepository _usersRepo;
    private readonly ILogger<SubmittedRequestsController> _log;

    public SubmittedRequestsController(
        SubmittedRequestFormRepository formsRepo,
        UsersRepository usersRepo,
        ILogger<SubmittedRequestsController> log)
    {
        _formsRepo = formsRepo;
        _usersRepo = usersRepo;
        _log = log;
    }
    using System.Globalization; // at top for date parsing

// ...

[HttpGet]
[Authorize]
public async Task<IActionResult> List(
    [FromQuery] bool all = false,
    [FromQuery] string? status = null,
    [FromQuery] string? from = null,   // yyyy-MM-dd (local date from UI)
    [FromQuery] string? to = null,     // yyyy-MM-dd (local date from UI)
    [FromQuery] int skip = 0,
    [FromQuery] int take = 500)
{
    // Resolve caller's badge (claims transformer now sets it)
    var badge = TryGetBadgeFromClaims(User);

    // Fallback via email -> Users table (belt-and-suspenders)
    if (string.IsNullOrWhiteSpace(badge))
    {
        var email = TryGetEmailFromClaims(User);
        if (!string.IsNullOrWhiteSpace(email))
        {
            var userRow = await _usersRepo.GetByEmailAsync(email);
            badge = userRow?.Badge;
        }
    }
    if (string.IsNullOrWhiteSpace(badge))
    {
        return Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: "Missing 'badge' claim and unable to resolve badge from email.");
    }

    // Determine if caller is admin
    var caller = await _usersRepo.GetByBadgeAsync(badge!);
    var isAdmin = (caller?.IsAdmin ?? 0) == 1;

    // Only admins may set all=true; non-admins are forced to mine-only
    string? createdByFilter = (isAdmin && all) ? null : badge;

    // Parse date-only filters (UI sends yyyy-MM-dd). Treat them as local-midnight ranges,
    // but convert here as UTC ranges that match your DB's UTC timestamps.
    DateTime? fromUtc = null;
    DateTime? toUtc = null;

    if (!string.IsNullOrWhiteSpace(from))
    {
        if (DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var f))
        {
            fromUtc = f.ToUniversalTime();
        }
    }
    if (!string.IsNullOrWhiteSpace(to))
    {
        if (DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t))
        {
            // add one day and use as exclusive upper bound
            toUtc = t.AddDays(1).ToUniversalTime();
        }
    }

    var rows = await _formsRepo.ListAsync(
        createdByFilter,
        fromUtc,
        toUtc,
        string.IsNullOrWhiteSpace(status) || status == "All" ? null : status,
        skip, take);

    // Shape for the UI: ISO date is easy to format client-side
    var result = rows.Select(r => new
    {
        id = r.Id,
        submitter = r.Submitter,
        createdDate = r.CreatedDate, // UTC
        status = r.Status
    });

    return Ok(result);
}

[HttpPost]
    public async Task<IActionResult> Create([FromBody] SubmitRequestFormRequest req)
    {
        // 1) Badge from token claims
        var badge = TryGetBadgeFromClaims(User);

        // 1a) DEV override header (lets you test locally even if claims/lookup fail)
        //     Send header x-badge-debug: 87100 from the SPA (Development only recommended).
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(badge) && string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase))
        {
            var hdrBadge = Request.Headers["x-badge-debug"].ToString();
            if (!string.IsNullOrWhiteSpace(hdrBadge))
            {
                badge = hdrBadge;
            }
        }

        // 2) Email/UPN fallback via Users repo
        if (string.IsNullOrWhiteSpace(badge))
        {
            var email = TryGetEmailFromClaims(User);
            if (!string.IsNullOrWhiteSpace(email))
            {
                var userRow = await _usersRepo.GetByEmailAsync(email);
                badge = userRow?.Badge;
            }
        }

        // 3) Still no badge? 403 with helpful message.
        if (string.IsNullOrWhiteSpace(badge))
        {
            _log.LogWarning("Forbidden submit: missing badge and unable to resolve from email. sub={Sub} upn={Upn} email={Email}",
                User.FindFirst("sub")?.Value, User.FindFirst("upn")?.Value, User.FindFirst("email")?.Value);

            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "Missing 'badge' claim and unable to resolve badge from email."
            );
        }

        // 4) Insert
        try
        {
            var id = await _formsRepo.InsertAsync(req.RequestFormId, badge!, req.SubmittedRequestData);
            return Created($"/api/submitted-requests/{id}", new { id });
        }
        catch (ArgumentException badJson)
        {
            return BadRequest(badJson.Message);
        }
        catch (InvalidOperationException fk)
        {
            // FK to dbo.Users(Badge) failed
            return BadRequest(fk.Message);
        }
    }

    // ---- helpers ----
    private static string? TryGetBadgeFromClaims(ClaimsPrincipal user) =>
        user.FindFirst("badge")?.Value
        ?? user.FindFirst("employee_badge")?.Value
        ?? user.FindFirst("employeeId")?.Value
        ?? user.FindFirst("employeeid")?.Value;

    private static string? TryGetEmailFromClaims(ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value
        ?? user.FindFirst(ClaimTypes.Email)?.Value
        ?? user.FindFirst("email")?.Value
        ?? user.FindFirst("upn")?.Value;
}
