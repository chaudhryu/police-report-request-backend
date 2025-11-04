using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using police_report_request_backend.Contracts.Requests;
using police_report_request_backend.Data;
using police_report_request_backend.Email;
using police_report_request_backend.Storage;
using police_report_request_backend.Helpers;

namespace police_report_request_backend.Controllers;

[ApiController]
[Route("submitted-requests")] // <— removed "api/"
[Authorize]
public sealed class SubmittedRequestsController : ControllerBase
{
    // Allow Closed and be case-insensitive
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    { "Submitted", "In Progress", "Completed", "Closed" };

    private readonly SubmittedRequestFormRepository _formsRepo;
    private readonly UsersRepository _usersRepo;
    private readonly IBlobUploadService _uploadSvc;
    private readonly IEmailNotificationService _mailer;
    private readonly IStorageSasService _sas;          // for read SAS
    private readonly BlobStorageOptions _storageOpts;  // to read ReadSasDays
    private readonly ILogger<SubmittedRequestsController> _log;

    public SubmittedRequestsController(
        SubmittedRequestFormRepository formsRepo,
        UsersRepository usersRepo,
        IBlobUploadService uploadSvc,
        IEmailNotificationService mailer,
        IStorageSasService sas,
        IOptions<BlobStorageOptions> storageOpts,
        ILogger<SubmittedRequestsController> log)
    {
        _formsRepo = formsRepo;
        _usersRepo = usersRepo;
        _uploadSvc = uploadSvc;
        _mailer = mailer;
        _sas = sas;
        _storageOpts = storageOpts.Value;
        _log = log;
    }

    // ---------- LIST ----------
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] bool all = false,
        [FromQuery] string? status = null,
        [FromQuery] string? from = null, // yyyy-MM-dd local
        [FromQuery] string? to = null,   // yyyy-MM-dd local
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
                DateTimeStyles.AssumeLocal, out var f)) fromUtc = f.ToUniversalTime();

        if (!string.IsNullOrWhiteSpace(to) &&
            DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var t)) toUtc = t.AddDays(1).ToUniversalTime();

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
            title = r.Title
        });

        return Ok(result);
    }

    // ---------- DETAILS (admin) ----------
    // Adds a short-lived downloadUrl to each attachments[i] in the returned JSON (not persisted).
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var badge = TryGetBadgeFromClaims(User) ?? await BadgeFromUsersByEmailAsync(User);
        if (!await IsAdminAsync(badge)) return AdminOnly();

        var d = await _formsRepo.GetDetailsAsync(id);
        if (d is null) return NotFound();

        // Inject downloadUrl into attachments using a read SAS
        var jsonWithLinks = AddDownloadLinksToAttachments(
            d.SubmittedRequestDataJson,
            lifetime: TimeSpan.FromDays(_storageOpts.ReadSasDays));

        object dataObj;
        try { dataObj = JsonSerializer.Deserialize<object>(jsonWithLinks) ?? new { }; }
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

    // ---------- UPDATE STATUS (admin) ----------
    // Accepts optional AdminNote and includes it in user emails for: In Progress, Completed, Closed
    [HttpPut("{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] UpdateSubmittedRequestStatusRequest body)
    {
        var badge = TryGetBadgeFromClaims(User) ?? await BadgeFromUsersByEmailAsync(User);
        if (!await IsAdminAsync(badge)) return AdminOnly();

        if (body is null || string.IsNullOrWhiteSpace(body.Status))
            return BadRequest("Status is required.");

        if (!AllowedStatuses.Contains(body.Status))
            return BadRequest($"Invalid status. Allowed: {string.Join(", ", AllowedStatuses)}");

        var submissionBeforeChange = await _formsRepo.GetDetailsAsync(id);
        if (submissionBeforeChange is null) return NotFound();

        var oldStatus = submissionBeforeChange.Status;
        var newStatus = body.Status;

        // Normalize & guard the admin note
        var adminNote = (body.AdminNote ?? "").Trim();
        if (adminNote.Length > 2000) adminNote = adminNote[..2000];

        // If no-op, do nothing (no emails)
        if (string.Equals(oldStatus, newStatus, StringComparison.OrdinalIgnoreCase))
            return NoContent();

        var changed = await _formsRepo.UpdateStatusAsync(id, newStatus, badge!);
        if (changed == 0) return NotFound();

        // Load updated details for email context
        var d = await _formsRepo.GetDetailsAsync(id);
        if (d is not null)
        {
            string? location = null;
            string detailsText = "";
            List<EmailAttachmentInfo> attach = new();

            try
            {
                using var doc = JsonDocument.Parse(d.SubmittedRequestDataJson);
                (location, detailsText) = DeriveIncident(doc.RootElement);

                if (doc.RootElement.TryGetProperty("attachments", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        var container = e.TryGetProperty("container", out var c) ? c.GetString() : null;
                        var blobName = e.TryGetProperty("blobName", out var b) ? b.GetString() : null;
                        var fileName = e.TryGetProperty("fileName", out var fn) ? fn.GetString() : "file";
                        var contentType = e.TryGetProperty("contentType", out var ct) ? ct.GetString() : "application/octet-stream";
                        var length = e.TryGetProperty("length", out var ln) && ln.TryGetInt64(out var L) ? L : 0L;

                        if (!string.IsNullOrWhiteSpace(container) && !string.IsNullOrWhiteSpace(blobName))
                        {
                            attach.Add(new EmailAttachmentInfo
                            {
                                Container = container!,
                                BlobName = blobName!,
                                FileName = fileName ?? "file",
                                ContentType = contentType ?? "application/octet-stream",
                                Length = length,
                                UploadedUtc = DateTime.UtcNow
                            });
                        }
                    }
                }
            }
            catch { /* ignore */ }

            var submitter = await _usersRepo.GetByBadgeAsync(d.CreatedBy);
            var adminUser = await _usersRepo.GetByBadgeAsync(badge!);

            if (newStatus.Equals("In Progress", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _log.LogInformation("Scheduling IN PROGRESS email for submission {Id}", id);

                        var emailContext = new SubmissionInProgressEmailContext
                        {
                            SubmissionId = id,
                            SubmitterEmail = submitter?.Email ?? "",
                            SubmitterDisplayName = submitter?.DisplayName ?? d.CreatedBy,
                            CreatedUtc = d.CreatedDate.ToUniversalTime(),
                            Location = location,
                            Title = location,
                            IncidentDetailsText = detailsText,
                            Attachments = attach,
                            AdminNote = string.IsNullOrWhiteSpace(adminNote) ? null : adminNote
                        };

                        await _mailer.SendSubmissionInProgressAsync(emailContext);
                        _log.LogInformation("In Progress email dispatched for submission {Id}", id);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "In Progress email failed for submission {Id}", id);
                    }
                });
            }
            else if (newStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _log.LogInformation("Scheduling COMPLETED email for submission {Id}", id);

                        var emailContext = new SubmissionCompletedEmailContext
                        {
                            SubmissionId = id,
                            SubmitterEmail = submitter?.Email ?? "",
                            SubmitterDisplayName = submitter?.DisplayName ?? d.CreatedBy,
                            AdminEmail = adminUser?.Email ?? "",
                            CompletedUtc = DateTime.UtcNow,
                            Location = location,
                            Title = location,
                            IncidentDetailsText = detailsText,
                            Attachments = attach,
                            AdminNote = string.IsNullOrWhiteSpace(adminNote) ? null : adminNote
                        };

                        await _mailer.SendSubmissionCompletedAsync(emailContext);
                        _log.LogInformation("Completion email dispatched for submission {Id}", id);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Completion email failed for submission {Id}", id);
                    }
                });
            }
            else if (newStatus.Equals("Closed", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _log.LogInformation("Scheduling CLOSED email for submission {Id}", id);

                        var emailContext = new SubmissionClosedEmailContext
                        {
                            SubmissionId = id,
                            SubmitterEmail = submitter?.Email ?? "",
                            SubmitterDisplayName = submitter?.DisplayName ?? d.CreatedBy,
                            AdminEmail = adminUser?.Email ?? "",
                            ClosedUtc = DateTime.UtcNow,
                            Location = location,
                            Title = location,
                            IncidentDetailsText = detailsText,
                            Attachments = attach,
                            AdminNote = string.IsNullOrWhiteSpace(adminNote) ? null : adminNote
                        };

                        await _mailer.SendSubmissionClosedAsync(emailContext);
                        _log.LogInformation("Closed email dispatched for submission {Id}", id);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Closed email failed for submission {Id}", id);
                    }
                });
            }
        }

        return NoContent();
    }

    // ---------- CREATE (multipart: json + files) ----------
    [HttpPost("multipart")]
    [Consumes("multipart/form-data")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 52_428_800)] // ~50 MB
    public async Task<IActionResult> CreateMultipart([FromForm] string data)
    {
        var badge = TryGetBadgeFromClaims(User) ?? await BadgeFromUsersByEmailAsync(User);
        if (string.IsNullOrWhiteSpace(badge))
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden",
                detail: "Missing 'badge' claim and unable to resolve badge from email.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch
        {
            return BadRequest("Invalid JSON in 'data' form field.");
        }

        var files = Request.Form.Files?.ToList() ?? new List<IFormFile>();
        List<EmailAttachmentInfo> saved = new();
        if (files.Count > 0)
        {
            saved = await _uploadSvc.SaveManyAsync(files, badge!, submissionId: null, role: "user", ct: HttpContext.RequestAborted);
        }

        // merge attachments (role=user)
        var mergedJson = MergeAttachmentsIntoJson(doc.RootElement, saved, roleForNew: "user");

        using var mergedDoc = JsonDocument.Parse(mergedJson);
        var id = await _formsRepo.InsertAsync(badge!, mergedDoc.RootElement);

        var submitter = await _usersRepo.GetByBadgeAsync(badge!);
        var (location, detailsText) = DeriveIncident(mergedDoc.RootElement);

        _ = Task.Run(async () =>
        {
            try
            {
                await _mailer.SendSubmissionNotificationsAsync(new SubmissionEmailContext
                {
                    SubmissionId = id,
                    SubmitterEmail = submitter?.Email ?? "",
                    SubmitterDisplayName = submitter?.DisplayName ?? badge!,
                    CreatedUtc = DateTime.UtcNow,
                    Location = location,
                    Title = location, // back-compat
                    IncidentDetailsText = detailsText,
                    Attachments = saved
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Email notify failed for submission {Id}", id);
            }
        });

        // IMPORTANT: do NOT include "/api" here (PathBase adds it)
        return Created($"/submitted-requests/{id}", new { id });
    }

    // ---------- ADMIN: append attachments ----------
    [HttpPost("{id:int}/attachments")]
    [Consumes("multipart/form-data")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 52_428_800)]
    public async Task<IActionResult> RegisterAttachments(int id)
    {
        var badge = TryGetBadgeFromClaims(User) ?? await BadgeFromUsersByEmailAsync(User);
        if (!await IsAdminAsync(badge)) return AdminOnly();

        var d = await _formsRepo.GetDetailsAsync(id);
        if (d is null) return NotFound();

        var files = Request.Form.Files?.ToList() ?? new List<IFormFile>();
        if (files.Count == 0) return BadRequest("No files.");

        var saved = await _uploadSvc.SaveManyAsync(files, badge!, submissionId: id, role: "ops", ct: HttpContext.RequestAborted);

        using var doc = JsonDocument.Parse(d.SubmittedRequestDataJson);
        var updated = MergeAttachmentsIntoJson(doc.RootElement, saved, roleForNew: "ops");

        await _formsRepo.UpdateSubmittedRequestDataJsonAsync(id, updated);
        return NoContent();
    }

    // ===== JSON helpers =====
    private static string MergeAttachmentsIntoJson(JsonElement root, List<EmailAttachmentInfo> newOnes, string? roleForNew = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();

            foreach (var p in root.EnumerateObject())
            {
                if (p.NameEquals("attachments")) continue; // we’ll rewrite it
                p.WriteTo(w);
            }

            var existing = root.TryGetProperty("attachments", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().ToList()
                : new List<JsonElement>();

            w.WritePropertyName("attachments");
            w.WriteStartArray();

            foreach (var e in existing) e.WriteTo(w);

            foreach (var a in newOnes)
            {
                w.WriteStartObject();
                w.WriteString("container", a.Container);
                w.WriteString("blobName", a.BlobName);
                w.WriteString("fileName", a.FileName);
                w.WriteString("contentType", a.ContentType);
                w.WriteNumber("length", a.Length);

                if (!string.IsNullOrWhiteSpace(roleForNew))
                    w.WriteString("role", roleForNew);

                var uploadedIso = (a.UploadedUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("o");
                w.WriteString("uploadedUtc", uploadedIso);

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    // Inject a read SAS URL into each attachments[i] for the response only.
    private string AddDownloadLinksToAttachments(string originalJson, TimeSpan lifetime)
    {
        try
        {
            using var doc = JsonDocument.Parse(originalJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return originalJson;

            using var ms = new MemoryStream();
            using var w = new Utf8JsonWriter(ms);

            w.WriteStartObject();
            foreach (var p in root.EnumerateObject())
            {
                if (!p.NameEquals("attachments"))
                {
                    p.WriteTo(w);
                    continue;
                }

                w.WritePropertyName("attachments");
                if (p.Value.ValueKind != JsonValueKind.Array)
                {
                    p.Value.WriteTo(w);
                    continue;
                }

                w.WriteStartArray();
                foreach (var item in p.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        item.WriteTo(w);
                        continue;
                    }

                    string? container = null, blob = null;

                    // Copy original props
                    using var objMs = new MemoryStream();
                    using var ow = new Utf8JsonWriter(objMs);
                    ow.WriteStartObject();

                    foreach (var ip in item.EnumerateObject())
                    {
                        ip.WriteTo(ow);
                        if (ip.Value.ValueKind == JsonValueKind.String)
                        {
                            if (ip.Name.Equals("container", StringComparison.OrdinalIgnoreCase))
                                container = ip.Value.GetString();
                            else if (ip.Name.Equals("blobName", StringComparison.OrdinalIgnoreCase))
                                blob = ip.Value.GetString();
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(container) && !string.IsNullOrWhiteSpace(blob))
                    {
                        var uri = _sas.CreateReadSasUri(container!, blob!, lifetime);
                        ow.WriteString("downloadUrl", uri.ToString());
                    }

                    ow.WriteEndObject();
                    ow.Flush();

                    using var objDoc = JsonDocument.Parse(objMs.ToArray());
                    objDoc.RootElement.WriteTo(w);
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.Flush();

            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return originalJson;
        }
    }

    // ===== incident derivation =====
    private static (string? Location, string DetailsText) DeriveIncident(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return (null, "");
        string? location = FirstNonEmpty(data,
            "incidentCrossings", "crossStreets",
            "incidentAddress", "address",
            "incidentStreetNames", "streetNames",
            "location");

        var lines = new List<string>();
        Add("Case Number", FirstNonEmpty(data, "caseNumber", "incidentNumber", "reportNumber"));
        Add("Incident Type", FirstNonEmpty(data, "incidentType", "type", "category"));
        var incidentDateStr = FirstNonEmpty(data, "incidentDateTime", "incidentDate", "date", "time");
        Add("Incident Date/Time", DateFormatter.ToFriendlyPacificTime(incidentDateStr));
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
            {
                if (TryGetStringCaseInsensitive(obj, n, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
            return null;
        }
    }

    private static bool TryGetStringCaseInsensitive(JsonElement obj, string name, out string? value)
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
    public string? AdminNote { get; set; }   // ← stays
}
