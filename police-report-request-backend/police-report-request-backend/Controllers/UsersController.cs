// Controllers/UsersController.cs
using System.Linq;                                   // For .Select(...)
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Abstractions;               // IDownstreamApi
using police_report_request_backend.Data;
using police_report_request_backend.Models;

namespace police_report_request_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // class-level: endpoints require an authenticated user; admin is checked in-code where needed
public sealed class UsersController : ControllerBase
{
    private readonly UsersRepository _repo;
    private readonly ILogger<UsersController> _logger;
    private readonly IDownstreamApi _graphApi;       // new interface (not the obsolete IDownstreamWebApi)
    private readonly IConfiguration _config;

    // Optional: if you configure Authorization:AdminGroupObjectIds in appsettings, we will elevate admins from AAD groups.
    private readonly string[] _adminGroupObjectIds;

    public UsersController(
        UsersRepository repo,
        ILogger<UsersController> logger,
        IDownstreamApi graphApi,
        IConfiguration config)
    {
        _repo = repo;
        _logger = logger;
        _graphApi = graphApi;
        _config = config;
        _adminGroupObjectIds = _config.GetSection("Authorization:AdminGroupObjectIds").Get<string[]>() ?? Array.Empty<string>();
    }

    // ---------- Shapes for Graph responses ----------
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

    // ---------- DTOs ----------
    public record UpsertUserDto(
        string Badge,
        string? FirstName,
        string? LastName,
        string? DisplayName,
        string? Email,
        string? Position,
        int? IsAdmin // ignored from client; server is the source of truth
    );

    public record SetAdminDto(bool IsAdmin);

    // ---------- Helpers ----------
    private static string? ClaimVal(ClaimsPrincipal user, string type)
        => user.FindFirst(type)?.Value;

    private static string? TryGetBadgeFromClaims(ClaimsPrincipal user) =>
        user.FindFirst("badge")?.Value
        ?? user.FindFirst("employee_badge")?.Value
        ?? user.FindFirst("employeeId")?.Value
        ?? user.FindFirst("employeeid")?.Value;

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

    // ---------- POST api/users/upsert ----------
    // Called by SPA after login; upserts the user row and returns isAdmin to shape the client UI.
    [HttpPost("upsert")]
    public async Task<IActionResult> Upsert([FromBody] UpsertUserDto dto)
    {
        var correlationId = GetCorrelationId();
        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            if (string.IsNullOrWhiteSpace(dto.Badge))
                return BadRequest("Badge is required.");

            var actorEmail = PreferredEmailFromToken(User);
            var actorOid = ClaimVal(User, "oid");

            _logger.LogInformation("Upsert started by {Actor} (oid={Oid}). Badge={Badge}", actorEmail, actorOid, dto.Badge);

            // Preserve existing IsAdmin from DB
            var existing = await _repo.GetByBadgeAsync(dto.Badge);
            var existingIsAdmin = (existing?.IsAdmin ?? 0) == 1;

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

            // Enrich from Graph only if missing fields; never fail if Graph/OBO is unavailable.
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
                    var resp = await _graphApi.CallApiForUserAsync(
                        "Graph",
                        opts => { opts.RelativePath = "me?$select=displayName,givenName,surname,mail,userPrincipalName,jobTitle,employeeId,officeLocation"; },
                        User);

                    if (resp.IsSuccessStatusCode)
                    {
                        var me = await resp.Content.ReadFromJsonAsync<GraphMe>();
                        if (me is not null)
                        {
                            row.Email ??= me.Mail ?? me.UserPrincipalName;
                            row.FirstName ??= me.GivenName;
                            row.LastName ??= me.Surname;
                            row.DisplayName ??= me.DisplayName
                                ?? string.Join(' ', new[] { me.GivenName, me.Surname }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                            row.Position ??= me.JobTitle;

                            _logger.LogInformation("Graph enrichment OK. Email={Email} DisplayName={DisplayName} Position={Position}",
                                row.Email, row.DisplayName, row.Position);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Graph /me HTTP {Status}; skipping enrichment.", (int)resp.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "Graph enrichment failed; skipping.");
                }
            }

            // Optional: map admin from AAD groups if configured
            if (AdminMappingFromGroupsEnabled)
            {
                try
                {
                    var body = new { groupIds = _adminGroupObjectIds };
                    using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                    var checkResp = await _graphApi.CallApiForUserAsync(
                        "Graph",
                        opts =>
                        {
                            opts.RelativePath = "me/checkMemberGroups";
                            opts.HttpMethod = "POST"; // string, per IDownstreamApi contract
                        },
                        User,
                        content);

                    if (checkResp.IsSuccessStatusCode)
                    {
                        var check = await checkResp.Content.ReadFromJsonAsync<CheckMemberGroupsResponse>();
                        var isInAny = (check?.Value?.Count ?? 0) > 0;

                        if (isInAny && row.IsAdmin == 0)
                        {
                            _logger.LogInformation("User is member of configured admin group(s); elevating.");
                            row.IsAdmin = 1;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("checkMemberGroups HTTP {Status}; preserving IsAdmin={IsAdmin}.",
                            (int)checkResp.StatusCode, row.IsAdmin);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "checkMemberGroups failed; preserving IsAdmin={IsAdmin}.", row.IsAdmin);
                }
            }

            await _repo.UpsertAsync(row, actorEmail);
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

    // ---------- GET api/users/{badge} ----------
    [HttpGet("{badge}")]
    public async Task<IActionResult> GetByBadge(string badge)
    {
        if (string.IsNullOrWhiteSpace(badge)) return BadRequest("Badge is required.");
        var user = await _repo.GetByBadgeAsync(badge);
        return user is not null ? Ok(user) : NotFound();
    }

    // ---------- DELETE api/users/{badge} ----------
    // Server-side admin check (no policy required).
    [HttpDelete("{badge}")]
    public async Task<IActionResult> Delete(string badge)
    {
        if (string.IsNullOrWhiteSpace(badge)) return BadRequest("Badge is required.");

        // Determine caller and ensure admin
        var actorBadge = TryGetBadgeFromClaims(User);
        UserRow? actor = null;
        if (!string.IsNullOrWhiteSpace(actorBadge))
            actor = await _repo.GetByBadgeAsync(actorBadge!);
        if (actor is null)
            actor = await _repo.GetByEmailAsync(PreferredEmailFromToken(User));

        if ((actor?.IsAdmin ?? 0) != 1)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "Admins only.");

        // Optional: prevent self-delete
        if (string.Equals(actor?.Badge, badge?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not allowed", detail: "You cannot delete your own account.");

        var rows = await _repo.DeleteAsync(badge.Trim());
        return rows > 0 ? NoContent() : NotFound();
    }

    // ---------- PUT api/users/{badge}/admin ----------
    // Toggle admin; server-side admin check; prevents self-demotion.
    [HttpPut("{badge}/admin")]
    public async Task<IActionResult> SetAdmin(string badge, [FromBody] SetAdminDto dto)
    {
        if (string.IsNullOrWhiteSpace(badge)) return BadRequest("Badge is required.");

        // Resolve actor (badge preferred, else email)
        var actorBadge = TryGetBadgeFromClaims(User);
        UserRow? actor = null;
        if (!string.IsNullOrWhiteSpace(actorBadge))
            actor = await _repo.GetByBadgeAsync(actorBadge!);
        if (actor is null)
            actor = await _repo.GetByEmailAsync(PreferredEmailFromToken(User));

        var isActorAdmin = (actor?.IsAdmin ?? 0) == 1;
        if (!isActorAdmin)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "Admins only.");

        // Prevent self-demotion (optional, recommended)
        if (string.Equals(actor?.Badge, badge?.Trim(), StringComparison.OrdinalIgnoreCase) && dto.IsAdmin == false)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not allowed", detail: "You cannot remove your own admin access.");
        }

        var changed = await _repo.SetAdminAsync(badge.Trim(), dto.IsAdmin, PreferredEmailFromToken(User));
        return changed > 0 ? Ok(new { badge = badge.Trim(), isAdmin = dto.IsAdmin }) : NotFound();
    }

    // ---------- GET api/users ----------
    // Admin-only list with optional search + paging.
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? q = null, [FromQuery] int skip = 0, [FromQuery] int take = 200)
    {
        // Server-side admin check via Users table
        var actorBadge = TryGetBadgeFromClaims(User);
        UserRow? actor = null;
        if (!string.IsNullOrWhiteSpace(actorBadge))
            actor = await _repo.GetByBadgeAsync(actorBadge!);
        if (actor is null)
            actor = await _repo.GetByEmailAsync(PreferredEmailFromToken(User));

        var isAdmin = (actor?.IsAdmin ?? 0) == 1;
        if (!isAdmin)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "Admins only.");

        var users = await _repo.ListAsync(q, skip, take);

        var result = users.Select(u => new
        {
            badge = u.Badge,
            firstName = u.FirstName ?? "",
            lastName = u.LastName ?? "",
            displayName = u.DisplayName ?? "",
            email = u.Email ?? "",
            position = u.Position ?? "",
            isAdmin = (u.IsAdmin ?? 0) == 1
        });

        return Ok(result);
    }
}
