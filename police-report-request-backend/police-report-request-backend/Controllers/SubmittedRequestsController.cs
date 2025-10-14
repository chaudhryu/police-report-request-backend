using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using police_report_request_backend.Contracts.Requests;
using police_report_request_backend.Data;
using police_report_request_backend.Email;

namespace police_report_request_backend.Controllers;

[ApiController]
[Route("api/submitted-requests")]
[Authorize]
public sealed class SubmittedRequestsController : ControllerBase
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    { "Submitted", "In Review", "In Progress", "Completed", "Approved", "Rejected", "Closed" };

    private readonly SubmittedRequestFormRepository _formsRepo;
    private readonly UsersRepository _usersRepo;
    private readonly IEmailNotificationService _mailer;
    private readonly ILogger<SubmittedRequestsController> _log;

    public SubmittedRequestsController(
        SubmittedRequestFormRepository formsRepo,
        UsersRepository usersRepo,
        IEmailNotificationService mailer,
        ILogger<SubmittedRequestsController> log)
    {
        _formsRepo = formsRepo;
        _usersRepo = usersRepo;
        _mailer = mailer;
        _log = log;
    }

    // GET api/submitted-requests?all=false&status=Submitted&from=2025-09-01&to=2025-09-30&skip=0&take=500
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] bool all = false,
        [FromQuery] string? status = null,
        [FromQuery] string? from = null,   // yyyy-MM-dd local
        [FromQuery] string? to = null,     // yyyy-MM-dd local
        [FromQuery] int skip = 0,
        [FromQuery] int take = 500)
    {
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

        var caller = await _usersRepo.GetByBadgeAsync(badge!);
        var isAdmin = (caller?.IsAdmin ?? 0) == 1;

        string? createdByFilter = (isAdmin && all) ? null : badge;

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
            toUtc = t.AddDays(1).ToUniversalTime(); // exclusive
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
            createdDate = r.CreatedDate,
            status = r.Status,
            title = r.Title // NOTE: API payload unchanged; UI can rename to "Location" if desired
        });

        return Ok(result);
    }

    // GET api/submitted-requests/{id}  (admin-only)
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var badge = TryGetBadgeFromClaims(User) ?? await BadgeFromUsersByEmailAsync(User);
        if (!await IsAdminAsync(badge)) return AdminOnly();

        var d = await _formsRepo.GetDetailsAsync(id);
        if (d is null) return NotFound();

        object dataObj;
        try { dataObj = JsonSerializer.Deserialize<object>(d.SubmittedRequestDataJson) ?? new { }; }
        catch { dataObj = new { }; }

        return Ok(new
        {
            id = d.Id,
            createdBy = d.CreatedBy,
            createdByDisplayName = d.CreatedByDisplayName ?? d.CreatedBy,
            status = d.Status,
            createdDate = d.CreatedDate,
            lastUpdatedDate = d.LastUpdatedDate,
            submittedRequestData = dataObj
        });
    }

    // PUT api/submitted-requests/{id}/status  (admin-only)
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

        if (string.Equals(body.Status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            var d = await _formsRepo.GetDetailsAsync(id);
            if (d is not null)
            {
                // CHANGE: derive Location + details from stored JSON
                string? location = null;                // CHANGE
                string detailsText = "";                // CHANGE
                try
                {
                    using var doc = JsonDocument.Parse(d.SubmittedRequestDataJson);
                    (location, detailsText) = DeriveIncident(doc.RootElement); // CHANGE
                }
                catch { /* ignore */ }

                var submitter = await _usersRepo.GetByBadgeAsync(d.CreatedBy);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        _log.LogInformation("Scheduling completion email for submission {Id}", id);
                        await _mailer.SendSubmissionNotificationsAsync(new SubmissionEmailContext
                        {
                            SubmissionId = id,
                            SubmitterEmail = submitter?.Email ?? "",
                            SubmitterDisplayName = submitter?.DisplayName ?? d.CreatedBy,
                            CreatedUtc = d.CreatedDate.ToUniversalTime(), // CHANGE: send original created time
                            Location = location,                          // CHANGE: new preferred field
                            Title = location,                             // keep for back-compat in mailer
                            IncidentDetailsText = detailsText             // CHANGE: extra incident details
                        });
                        _log.LogInformation("Completion email dispatched for submission {Id}", id);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Completion email failed for submission {Id}", id);
                    }
                });
            }
        }

        return NoContent();
    }

    // POST api/submitted-requests
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SubmitRequestFormRequest req)
    {
        // 1) Resolve badge from claims / dev header / email
        var badge = TryGetBadgeFromClaims(User);

        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(badge) &&
            string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase))
        {
            var hdrBadge = Request.Headers["x-badge-debug"].ToString();
            if (!string.IsNullOrWhiteSpace(hdrBadge)) badge = hdrBadge;
        }

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
            _log.LogWarning("Forbidden submit: missing badge and unable to resolve from email. sub={Sub} upn={Upn} email={Email}",
                User.FindFirst("sub")?.Value, User.FindFirst("upn")?.Value, User.FindFirst("email")?.Value);

            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "Missing 'badge' claim and unable to resolve badge from email.");
        }

        // 2) Insert then fire-and-forget emails
        try
        {
            var id = await _formsRepo.InsertAsync(badge!, req.SubmittedRequestData);
            _log.LogInformation("Submission created: id={Id} by badge={Badge}", id, badge);

            var submitter = await _usersRepo.GetByBadgeAsync(badge!);

            // CHANGE: derive Location + details from incoming JSON
            var (location, detailsText) = DeriveIncident(req.SubmittedRequestData); // CHANGE

            _ = Task.Run(async () =>
            {
                try
                {
                    _log.LogInformation("Scheduling email notify for submission {Id}", id);
                    await _mailer.SendSubmissionNotificationsAsync(new SubmissionEmailContext
                    {
                        SubmissionId = id,
                        SubmitterEmail = submitter?.Email ?? "",
                        SubmitterDisplayName = submitter?.DisplayName ?? badge!,
                        CreatedUtc = DateTime.UtcNow,
                        Location = location,                  // CHANGE
                        Title = location,                     // keep for back-compat in mailer
                        IncidentDetailsText = detailsText     // CHANGE
                    });
                    _log.LogInformation("Email notify complete for submission {Id}", id);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Email notify failed for submission {Id}", id);
                }
            });

            return Created($"/api/submitted-requests/{id}", new { id });
        }
        catch (ArgumentException badJson)
        {
            return BadRequest(badJson.Message);
        }
        catch (InvalidOperationException fk)
        {
            return BadRequest(fk.Message);
        }
    }

    // ===== helpers =====

    // CHANGE: new helper to derive Location + a newline-separated incident details string (case-insensitive keys)
    private static (string? Location, string DetailsText) DeriveIncident(JsonElement data) // CHANGE
    {
        if (data.ValueKind != JsonValueKind.Object) return (null, "");

        // Prefer these fields (in order) for "Location"
        string? location = FirstNonEmpty(data,
            "incidentCrossings", "crossStreets",
            "incidentAddress", "address",
            "incidentStreetNames", "streetNames",
            "location");

        // Build additional details list
        var lines = new List<string>();
        Add("Case Number", FirstNonEmpty(data, "caseNumber", "incidentNumber", "reportNumber"));
        Add("Incident Type", FirstNonEmpty(data, "incidentType", "type", "category"));
        Add("Incident Date/Time", FirstNonEmpty(data, "incidentDateTime", "incidentDate", "date", "time"));
        Add("Address", FirstNonEmpty(data, "incidentAddress", "address"));
        Add("Cross Streets", FirstNonEmpty(data, "incidentCrossings", "crossStreets"));
        Add("Street Names", FirstNonEmpty(data, "incidentStreetNames", "streetNames"));
        Add("Description", FirstNonEmpty(data, "description", "details", "narrative"));

        return (location, string.Join('\n', lines));

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                lines.Add($"{label}: {value!.Trim()}");
        }

        static string? FirstNonEmpty(JsonElement obj, params string[] names)
        {
            foreach (var n in names)
                if (TryGetStringCaseInsensitive(obj, n, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            return null;
        }
    }

    // CHANGE: case-insensitive JSON string lookup
    private static bool TryGetStringCaseInsensitive(JsonElement obj, string name, out string? value) // CHANGE
    {
        value = null;
        if (obj.ValueKind != JsonValueKind.Object) return false;

        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    value = p.Value.GetString();
                    return true;
                }
                break;
            }
        }
        return false;
    }

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

public sealed class UpdateSubmittedRequestStatusRequest
{
    public string Status { get; set; } = default!;
}
