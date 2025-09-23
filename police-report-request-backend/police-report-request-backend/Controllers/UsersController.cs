// UsersController.cs
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web; // IDownstreamWebApi
using police_report_request_backend.Data;
using police_report_request_backend.Models;
using System.Linq;

namespace police_report_request_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class UsersController : ControllerBase
{
    private readonly UsersRepository _repo;
    private readonly ILogger<UsersController> _logger;
    private readonly IDownstreamWebApi _graphApi;
    private readonly IConfiguration _config;

    // If you set Authorization:AdminGroupObjectIds in appsettings.json, we can map admin from AAD groups.
    private readonly string[] _adminGroupObjectIds;

    public UsersController(
        UsersRepository repo,
        ILogger<UsersController> logger,
        IDownstreamWebApi graphApi,
        IConfiguration config)
    {
        _repo = repo;
        _logger = logger;
        _graphApi = graphApi;
        _config = config;
        _adminGroupObjectIds = _config.GetSection("Authorization:AdminGroupObjectIds").Get<string[]>() ?? Array.Empty<string>();
    }

    // --------------------------
    // Shapes for Graph responses
    // --------------------------
    private sealed class GraphMe
    {
        public string? DisplayName { get; set; }
        public string? GivenName { get; set; }
        public string? Surname { get; set; }
        public string? Mail { get; set; }
        public string? UserPrincipalName { get; set; }
        public string? JobTitle { get; set; }
        public string? EmployeeId { get; set; }
        public string? OfficeLocation { get; set; }
    }

    private sealed class CheckMemberGroupsResponse
    {
        public List<string>? Value { get; set; }
    }

    // --------------------------
    // DTOs
    // --------------------------
    public record UpsertUserDto(
        string Badge,
        string? FirstName,
        string? LastName,
        string? DisplayName,
        string? Email,
        string? Position,
        int? IsAdmin // IGNORED: we never trust the client for admin
    );

    public record SetAdminDto(bool IsAdmin);

    // --------------------------
    // Helpers
    // --------------------------
    private static string? ClaimVal(ClaimsPrincipal user, string type)
        => user.FindFirst(type)?.Value;

    private string PreferredEmailFromToken(ClaimsPrincipal user)
        => ClaimVal(user, "preferred_username")
           ?? ClaimVal(user, "unique_name")
           ?? ClaimVal(user, "upn")
           ?? ClaimVal(user, "email")
           ?? "unknown@unknown";

    private string GetCorrelationId()
        => Request.Headers["x-correlation-id"].FirstOrDefault()
           ?? Guid.NewGuid().ToString("N");

    private bool AdminMappingFromGroupsEnabled => _adminGroupObjectIds.Length > 0;

    // --------------------------
    // POST api/users/upsert
    // Called by SPA after login; returns isAdmin to render UI properly.
    // --------------------------
    [HttpPost("upsert")]
    [Authorize]
    public async Task<IActionResult> Upsert([FromBody] UpsertUserDto dto)
    {
        var correlationId = GetCorrelationId();

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            if (string.IsNullOrWhiteSpace(dto.Badge))
            {
                _logger.LogWarning("Upsert rejected: missing badge. Payload: {@Dto}", dto);
                return BadRequest("Badge is required.");
            }

            var actorEmail = PreferredEmailFromToken(User);
            var actorOid = ClaimVal(User, "oid");

            _logger.LogInformation("Upsert started by {Actor} (oid={Oid}). Badge={Badge}", actorEmail, actorOid, dto.Badge);

            // Preserve existing admin flag
            var existing = await _repo.GetByBadgeAsync(dto.Badge);
            var existingIsAdmin = existing?.IsAdmin == 1;

            // Build row from client payload (never trust client IsAdmin)
            var row = new UserRow
            {
                Badge = dto.Badge.Trim(),
                FirstName = dto.FirstName,
                LastName = dto.LastName,
                DisplayName = dto.DisplayName,
                Email = dto.Email,
                Position = dto.Position,
                IsAdmin = existingIsAdmin ? 1 : 0
            };

            // Enrich from Graph if important fields are missing
            bool needsGraph =
                string.IsNullOrWhiteSpace(row.Email) ||
                string.IsNullOrWhiteSpace(row.DisplayName) ||
                string.IsNullOrWhiteSpace(row.FirstName) ||
                string.IsNullOrWhiteSpace(row.LastName) ||
                string.IsNullOrWhiteSpace(row.Position);

            if (needsGraph)
            {
                try
                {
                    var resp = await _graphApi.CallWebApiForUserAsync("Graph", opts =>
                    {
                        // Keep it tight with $select to reduce latency/size
                        opts.RelativePath = "me?$select=displayName,givenName,surname,mail,userPrincipalName,jobTitle,employeeId,officeLocation";
                    });

                    resp.EnsureSuccessStatusCode();
                    var me = await resp.Content.ReadFromJsonAsync<GraphMe>();

                    if (me is not null)
                    {
                        row.Email ??= me.Mail ?? me.UserPrincipalName;
                        row.FirstName ??= me.GivenName;
                        row.LastName ??= me.Surname;
                        row.DisplayName ??= me.DisplayName
                            ?? string.Join(' ', new[] { me.GivenName, me.Surname }
                                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                        row.Position ??= me.JobTitle;

                        _logger.LogInformation("Graph enrichment OK. Email={Email} DisplayName={DisplayName} Position={Position}",
                            row.Email, row.DisplayName, row.Position);
                    }
                    else
                    {
                        _logger.LogWarning("Graph /me returned null; using token claim fallback");
                        row.Email ??= actorEmail;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Graph enrichment failed; using token claim fallback");
                    row.Email ??= actorEmail;
                }
            }
            else
            {
                row.Email ??= actorEmail;
            }

            // Optional: Map admin from AAD groups via /me/checkMemberGroups
            // NOTE: Content is passed as the 4th parameter to CallWebApiForUserAsync, not on options.
            if (AdminMappingFromGroupsEnabled)
            {
                try
                {
                    var body = new { groupIds = _adminGroupObjectIds };
                    using var content = new StringContent(
                        JsonSerializer.Serialize(body),
                        Encoding.UTF8,
                        "application/json");

                    var checkResp = await _graphApi.CallWebApiForUserAsync(
                        "Graph",
                        opts =>
                        {
                            opts.RelativePath = "me/checkMemberGroups";
                            opts.HttpMethod = HttpMethod.Post;
                        },
                        User,        // ClaimsPrincipal
                        content      // <-- body goes here
                    );

                    checkResp.EnsureSuccessStatusCode();
                    var check = await checkResp.Content.ReadFromJsonAsync<CheckMemberGroupsResponse>();
                    var isInAny = (check?.Value?.Count ?? 0) > 0;

                    if (isInAny && row.IsAdmin == 0)
                    {
                        _logger.LogInformation("User is member of configured admin group(s). Elevating to admin.");
                        row.IsAdmin = 1;
                    }
                    else if (!isInAny && row.IsAdmin == 1)
                    {
                        _logger.LogInformation("User is not in admin group(s). Preserving DB admin={IsAdmin}.", existingIsAdmin);
                        // Choose your policy: preserve DB, or de‑elevate to 0 if not in groups
                        // row.IsAdmin = 0;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "checkMemberGroups failed; preserving existing admin={Admin}", existingIsAdmin);
                }
            }

            await _repo.UpsertAsync(row, actorEmail);
            _logger.LogInformation("Upsert complete for badge {Badge}", row.Badge);

            // Re-read to return canonical values (esp. IsAdmin)
            var saved = await _repo.GetByBadgeAsync(row.Badge);

            return Ok(new
            {
                badge = saved?.Badge ?? row.Badge,
                displayName = saved?.DisplayName ?? row.DisplayName,
                email = saved?.Email ?? row.Email,
                isAdmin = (saved?.IsAdmin ?? row.IsAdmin) == 1,
                correlationId
            });
        }
    }

    // --------------------------
    // GET api/users/{badge}
    // --------------------------
    [HttpGet("{badge}")]
    [Authorize]
    public async Task<IActionResult> GetByBadge(string badge)
    {
        if (string.IsNullOrWhiteSpace(badge)) return BadRequest("Badge is required.");
        var user = await _repo.GetByBadgeAsync(badge);
        return user is not null ? Ok(user) : NotFound();
    }

    // --------------------------
    // DELETE api/users/{badge}
    // --------------------------
    [HttpDelete("{badge}")]
    [Authorize(Policy = "AdminsOnly")] // deleting users is admin-only
    public async Task<IActionResult> Delete(string badge)
    {
        if (string.IsNullOrWhiteSpace(badge)) return BadRequest("Badge is required.");
        var rows = await _repo.DeleteAsync(badge);
        return rows > 0 ? NoContent() : NotFound();
    }

    // --------------------------
    // PUT api/users/{badge}/admin
    // Optional helper to toggle admin in DB (UI tool).
    // Protected by AdminsOnly policy (claims-based).
    // --------------------------
    [HttpPut("{badge}/admin")]
    [Authorize(Policy = "AdminsOnly")]
    public async Task<IActionResult> SetAdmin(string badge, [FromBody] SetAdminDto dto)
    {
        if (string.IsNullOrWhiteSpace(badge)) return BadRequest("Badge is required.");
        var actor = PreferredEmailFromToken(User);
        var changed = await _repo.SetAdminAsync(badge.Trim(), dto.IsAdmin, actor);
        return changed > 0 ? Ok(new { badge, isAdmin = dto.IsAdmin }) : NotFound();
    }
}
