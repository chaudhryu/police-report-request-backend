using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using police_report_request_backend.Data;
using police_report_request_backend.Models;

namespace police_report_request_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly UsersRepository _repo;
    public UsersController(UsersRepository repo) => _repo = repo;

    // DTO the SPA sends (from FIS data)
    public record UpsertUserDto(
        string Badge,
        string? FirstName,
        string? LastName,
        string? DisplayName,
        string? Email,
        string? Position,
        int? IsAdmin
    );

    // POST: /api/users/upsert  (secured)
    [HttpPost("upsert")]
    [Authorize] // <-- keep it simple; requires a valid token for your Audience
    // If you kept the custom policy, you can use: [Authorize(Policy = "ApiScope")]
    public async Task<IActionResult> Upsert([FromBody] UpsertUserDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Badge))
            return BadRequest("Badge is required.");

        // Who is performing this write? Prefer the token's email-like claims
        var actor = HttpContext.User.FindFirst("preferred_username")?.Value
                 ?? HttpContext.User.FindFirst(ClaimTypes.Email)?.Value
                 ?? HttpContext.User.FindFirst("upn")?.Value
                 ?? "unknown@unknown";

        var row = new UserRow
        {
            Badge = dto.Badge,
            FirstName = dto.FirstName,
            LastName = dto.LastName,
            DisplayName = dto.DisplayName,
            Email = dto.Email,
            Position = dto.Position,
            IsAdmin = dto.IsAdmin ?? 0
        };

        await _repo.UpsertAsync(row, actor);
        return Ok(new { badge = row.Badge });
    }

    // Optional helpers for debugging:
    [HttpGet("{badge}")]
    [Authorize]
    public async Task<IActionResult> Get(string badge)
        => (await _repo.GetByBadgeAsync(badge)) is { } u ? Ok(u) : NotFound();

    [HttpDelete("{badge}")]
    [Authorize]
    public async Task<IActionResult> Delete(string badge)
        => (await _repo.DeleteAsync(badge)) > 0 ? NoContent() : NotFound();
}
