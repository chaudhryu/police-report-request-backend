using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
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
    // Allowed states (matches your DB CHECK constraint)
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    { "Submitted", "InReview", "Approved", "Rejected", "Closed", "Draft" };

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

    // GET api/submitted-requests?all=false&status=Submitted&from=2025-09-01&to=2025-09-30&skip=0&take=500
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] bool all = false,
        [FromQuery] string? status = null,
        [FromQuery] string? from = null,   // yyyy-MM-dd (local date from UI)
        [FromQuery] string? to = null,     // yyyy-MM-dd (local date from UI)
        [FromQuery] int skip = 0,
        [FromQuery] int take = 500)
    {
        // Resolve caller's badge (claims transformer should set this; fallback to Users by email)
        var badge = TryGetBadgeFromClaims(User) ?? await BadgeFromUsersByEmailAsync(User);

        if (string.IsNullOrWhiteSpace(badge))
        {
            _log.LogWarning("Forbidden list: missing badge; sub={Sub} upn={Upn} email={Email}",
                User.FindFirst("sub")?.Value, User.FindFirst("upn")?.Value, User.FindFirst("email")?.Value);

            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "Missing 'badge' claim and unable to resolve badge from email.");
        }

        // Determine admin
        var caller = await _usersRepo.GetByBadgeAsync(badge!);
        var isAdmin = (caller?.IsAdmin ?? 0) == 1;

        // Only admins may request all=true
        string? createdByFilter = (isAdmin && all) ? null : badge;

        // Parse date-only filters -> UTC range
        DateTime? fromUtc = null;
        DateTime? toUtc = null;

        if (!string.IsNullOrWhiteSpace(from) &&
            DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var f))
        {
            fromUtc = f.ToUniversalTime();
        }

        if (!string.IsNullOrWhiteSpace(to) &&
            DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var t))
        {
            toUtc = t.AddDays(1).ToUniversalTime(); // exclusive upper bound
        }

        var rows = await _formsRepo.ListAsync(
            createdByFilter,
            fromUtc,
            toUtc,
            string.IsNullOrWhiteSpace(status) || status == "All" ? null : status,
            skip,
            take);

        var result = rows.Select(r => new
        {
            id = r.Id,
            submitter = r.Submitter,
            createdDate = r.CreatedDate, // UTC ISO is fine for the client
            status = r.Status
        });

        return Ok(result);
    }

    // NEW: GET api/submitted-requests/{id}  (admin-only) — full record with submitted JSON
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var badge = TryGetBadgeFromClaims(User) ?? await BadgeFromUsersByEmailAsync(User);
        if (!await IsAdminAsync(badge)) return AdminOnly();

        var d = await _formsRepo.GetDetailsAsync(id);
        if (d is null) return NotFound();

        // Parse JSON payload for convenience
        object dataObj;
        try { dataObj = JsonSerializer.Deserialize<object>(d.SubmittedRequestDataJson) ?? new { }; }
        catch { dataObj = new { }; }

        return Ok(new
        {
            id = d.Id,
            requestFormId = d.RequestFormId,
            createdBy = d.CreatedBy,
            createdByDisplayName = d.CreatedByDisplayName ?? d.CreatedBy,
            status = d.Status,
            createdDate = d.CreatedDate,
            lastUpdatedDate = d.LastUpdatedDate,
            submittedRequestData = dataObj
        });
    }

    // NEW: PUT api/submitted-requests/{id}/status  (admin-only) — change status
    [HttpPut("{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] UpdateSubmittedRequestStatusRequest body)
    {
        var badge = TryGetBadgeFromClaims(User) ?? await BadgeFromUsersByEmailAsync(User);
        if (!await IsAdminAsync(badge)) return AdminOnly();

        if (body is null || string.IsNullOrWhiteSpace(body.Status))
            return BadRequest("Status is required.");

        if (!AllowedStatuses.Contains(body.Status))
            return BadRequest($"Invalid status. Allowed: {string.Join(", ", AllowedStatuses)}");

        var changed = await _formsRepo.UpdateStatusAsync(id, body.Status, badge!);
        if (changed == 0) return NotFound();

        return NoContent();
    }

    // POST api/submitted-requests
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SubmitRequestFormRequest req)
    {
        // 1) Badge from token claims
        var badge = TryGetBadgeFromClaims(User);

        // 1a) DEV override header (lets you test locally even if claims/lookup fail)
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(badge) &&
            string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase))
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
                detail: "Missing 'badge' claim and unable to resolve badge from email.");
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

    private async Task<string?> BadgeFromUsersByEmailAsync(ClaimsPrincipal user)
    {
        var email = TryGetEmailFromClaims(user);
        if (string.IsNullOrWhiteSpace(email)) return null;
        var u = await _usersRepo.GetByEmailAsync(email);
        return u?.Badge;
    }

    private async Task<bool> IsAdminAsync(string? badge)
    {
        if (string.IsNullOrWhiteSpace(badge)) return false;
        var me = await _usersRepo.GetByBadgeAsync(badge);
        return (me?.IsAdmin ?? 0) == 1;
    }

    private ObjectResult AdminOnly() =>
        Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "Admins only.");
}
