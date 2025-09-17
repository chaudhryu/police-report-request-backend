using System.Security.Claims;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web; // for IDownstreamWebApi
using police_report_request_backend.Data;
using police_report_request_backend.Models;
using System.Linq;

namespace police_report_request_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly UsersRepository _repo;
    private readonly ILogger<UsersController> _logger;
    private readonly IDownstreamWebApi _graphApi; // Call Graph via OBO (no Graph SDK)

    public UsersController(
        UsersRepository repo,
        ILogger<UsersController> logger,
        IDownstreamWebApi graphApi)
    {
        _repo = repo;
        _logger = logger;
        _graphApi = graphApi;
    }

    // Shape for /me fields we care about
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

    // DTO from SPA
    public record UpsertUserDto(
        string Badge,
        string? FirstName,
        string? LastName,
        string? DisplayName,
        string? Email,
        string? Position,
        int? IsAdmin
    );

    [HttpPost("upsert")]
    [Authorize]
    public async Task<IActionResult> Upsert([FromBody] UpsertUserDto dto)
    {
        var correlationId = Request.Headers["x-correlation-id"].FirstOrDefault()
                            ?? Guid.NewGuid().ToString("N");

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            if (string.IsNullOrWhiteSpace(dto.Badge))
            {
                _logger.LogWarning("Upsert rejected: missing badge. Payload: {@Dto}", dto);
                return BadRequest("Badge is required.");
            }

            string? ClaimVal(string t) => User.FindFirst(t)?.Value;
            var emailLike =
                ClaimVal("preferred_username") ??
                ClaimVal("unique_name") ??
                ClaimVal("upn") ??
                ClaimVal("email");

            var actor = emailLike ?? ClaimVal("name") ?? "unknown@unknown";
            var actorOid = ClaimVal("oid");

            _logger.LogInformation("Upsert by {Actor} (oid={Oid}). Name={Name} EmailClaim={Email} UPN={Upn}",
                actor, actorOid, ClaimVal("name"), ClaimVal("email"), ClaimVal("upn"));

            // Start with client data
            var row = new UserRow
            {
                Badge = dto.Badge,
                FirstName = dto.FirstName,
                LastName = dto.LastName,
                DisplayName = dto.DisplayName,
                Email = dto.Email,
                Position = dto.Position,
                IsAdmin = 0 // SECURITY: never trust client for admin
            };

            // Enrich from Graph if important bits are missing
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
                    // Call Graph /me with $select to keep it fast
                    var resp = await _graphApi.CallWebApiForUserAsync("Graph", opts =>
                    {
                        opts.RelativePath = "me?$select=displayName,givenName,surname,mail,userPrincipalName,jobTitle,employeeId,officeLocation";
                    });

                    resp.EnsureSuccessStatusCode();
                    GraphMe? me = await resp.Content.ReadFromJsonAsync<GraphMe>();

                    if (me is not null)
                    {
                        row.Email ??= me.Mail ?? me.UserPrincipalName;
                        row.FirstName ??= me.GivenName;
                        row.LastName ??= me.Surname;
                        row.DisplayName ??= me.DisplayName ??
                                            string.Join(' ', new[] { me.GivenName, me.Surname }
                                                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                        row.Position ??= me.JobTitle;

                        // If your "badge" comes from Graph, reconcile here if desired:
                        // row.Badge = me.OfficeLocation ?? me.EmployeeId ?? row.Badge;

                        _logger.LogInformation("Graph enrichment succeeded. Email={Email} DisplayName={DisplayName} Position={Position}",
                            row.Email, row.DisplayName, row.Position);
                    }
                    else
                    {
                        _logger.LogWarning("Graph /me returned null; using token fallback.");
                        row.Email ??= emailLike;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Graph enrichment failed. Proceeding with client payload + token claims.");
                    row.Email ??= emailLike;
                }
            }
            else
            {
                row.Email ??= emailLike;
            }

            await _repo.UpsertAsync(row, actor);
            _logger.LogInformation("Upsert complete for badge {Badge}", row.Badge);

            return Ok(new { badge = row.Badge, correlationId });
        }
    }

    // Optional helpers
    [HttpGet("{badge}")]
    [Authorize]
    public async Task<IActionResult> Get(string badge)
        => (await _repo.GetByBadgeAsync(badge)) is { } u ? Ok(u) : NotFound();

    [HttpDelete("{badge}")]
    [Authorize]
    public async Task<IActionResult> Delete(string badge)
        => (await _repo.DeleteAsync(badge)) > 0 ? NoContent() : NotFound();
}
