// Controllers/UsersController.cs
using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using police_report_request_backend.Data;
using police_report_request_backend.Models;

namespace police_report_request_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // All endpoints require an authenticated caller; admin is checked per-action
public sealed class UsersController : ControllerBase
{
    private readonly UsersRepository _repo;
    private readonly ILogger<UsersController> _logger;

    public UsersController(UsersRepository repo, ILogger<UsersController> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    // ------------ DTOs ------------
    public sealed class SetAdminDto
    {
        public bool IsAdmin { get; set; }
    }

    public sealed class AddAdminRequest
    {
        public string? Badge { get; set; }
        public string? Email { get; set; }
        public string? DisplayName { get; set; }
    }

    // ------------ Helpers ------------
    private static string? ClaimVal(ClaimsPrincipal user, string type) =>
        user.FindFirst(type)?.Value;

    private static string? TryGetBadgeFromClaims(ClaimsPrincipal user) =>
        user.FindFirst("badge")?.Value
        ?? user.FindFirst("employee_badge")?.Value
        ?? user.FindFirst("employeeId")?.Value
        ?? user.FindFirst("employeeid")?.Value;

    private string PreferredEmailFromToken(ClaimsPrincipal user) =>
        ClaimVal(user, "preferred_username")
        ?? ClaimVal(user, "unique_name")
        ?? ClaimVal(user, "upn")
        ?? ClaimVal(user, "email")
        ?? "system";

    private async Task<UserRow?> ResolveActorAsync()
    {
        var badge = TryGetBadgeFromClaims(User);
        if (!string.IsNullOrWhiteSpace(badge))
        {
            var byBadge = await _repo.GetByBadgeAsync(badge!);
            if (byBadge is not null) return byBadge;
        }

        var email = PreferredEmailFromToken(User);
        return await _repo.GetByEmailAsync(email);
    }

    private async Task<bool> EnsureActorIsAdminAsync()
    {
        var actor = await ResolveActorAsync();
        return (actor?.IsAdmin ?? 0) == 1;
    }

    // ------------ GET /api/users ------------
    // Admin-only list with optional search and paging
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? q = null, [FromQuery] int skip = 0, [FromQuery] int take = 200)
    {
        if (!await EnsureActorIsAdminAsync())
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "Admins only.");

        var users = await _repo.ListAsync(q, skip, take);

        var shaped = users.Select(u => new
        {
            badge = u.Badge,
            firstName = u.FirstName ?? "",
            lastName = u.LastName ?? "",
            displayName = u.DisplayName ?? "",
            email = u.Email ?? "",
            position = u.Position ?? "",
            isAdmin = (u.IsAdmin ?? 0) == 1
        });

        return Ok(shaped);
    }

    // ------------ PUT /api/users/{badge}/admin ------------
    // Toggle admin bit for a user (no implicit creation here). Prevent self-demotion.
    [HttpPut("{badge}/admin")]
    public async Task<IActionResult> SetAdmin([FromRoute] string badge, [FromBody] SetAdminDto body)
    {
        if (string.IsNullOrWhiteSpace(badge))
            return BadRequest("Badge is required.");

        var actor = await ResolveActorAsync();
        var isActorAdmin = (actor?.IsAdmin ?? 0) == 1;
        if (!isActorAdmin)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "Admins only.");

        if (string.Equals(actor?.Badge, badge.Trim(), StringComparison.OrdinalIgnoreCase) && body.IsAdmin == false)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not allowed", detail: "You cannot remove your own admin access.");

        var changed = await _repo.SetAdminAsync(badge.Trim(), body.IsAdmin, PreferredEmailFromToken(User));
        if (changed == 0) return NotFound("User not found.");

        _logger.LogInformation("Admin toggle by {Actor} -> Badge={Badge}, IsAdmin={IsAdmin}", actor?.Badge ?? "(unknown)", badge, body.IsAdmin);
        return NoContent();
    }

    // ------------ POST /api/users/admin ------------
    // Create or promote a user to admin. This is the only path that inserts a Users row.
    [HttpPost("admin")]
    public async Task<IActionResult> AddAdmin([FromBody] AddAdminRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Badge))
            return BadRequest("badge is required.");

        if (!await EnsureActorIsAdminAsync())
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "Admins only.");

        var actorEmail = PreferredEmailFromToken(User);

        await _repo.EnsureAdminAsync(
            req.Badge!.Trim(),
            string.IsNullOrWhiteSpace(req.Email) ? null : req.Email!.Trim(),
            string.IsNullOrWhiteSpace(req.DisplayName) ? null : req.DisplayName!.Trim(),
            actorEmail
        );

        var row = await _repo.GetByBadgeAsync(req.Badge!.Trim());
        if (row is null)
            return StatusCode(StatusCodes.Status500InternalServerError, "Admin was created but could not be read.");

        return Created($"/api/users/{row.Badge}", new
        {
            badge = row.Badge,
            firstName = row.FirstName ?? "",
            lastName = row.LastName ?? "",
            displayName = row.DisplayName ?? "",
            email = row.Email ?? "",
            position = row.Position ?? "",
            isAdmin = (row.IsAdmin ?? 0) == 1
        });
    }

    // (Optional) GET /api/users/{badge} — keep if you need it for tooling/UIs
    [HttpGet("{badge}")]
    public async Task<IActionResult> GetByBadge([FromRoute] string badge)
    {
        if (!await EnsureActorIsAdminAsync())
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "Admins only.");

        if (string.IsNullOrWhiteSpace(badge)) return BadRequest("Badge is required.");
        var row = await _repo.GetByBadgeAsync(badge.Trim());
        return row is null
            ? NotFound()
            : Ok(new
            {
                badge = row.Badge,
                firstName = row.FirstName ?? "",
                lastName = row.LastName ?? "",
                displayName = row.DisplayName ?? "",
                email = row.Email ?? "",
                position = row.Position ?? "",
                isAdmin = (row.IsAdmin ?? 0) == 1
            });
    }
}
