// Controllers/AdminStatsController.cs
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using police_report_request_backend.Data;
using police_report_request_backend.Models;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace police_report_request_backend.Controllers
{
    [ApiController]
    [Route("admin/stats")] // <— removed "api/" (PathBase("/api") will prefix it)
    [Authorize] // we'll still verify admin in-code per action
    [Produces("application/json")]
    public sealed class AdminStatsController : ControllerBase
    {
        private readonly SubmittedRequestFormRepository _forms;
        private readonly UsersRepository _users;
        private readonly IConfiguration _config;

        public AdminStatsController(
            SubmittedRequestFormRepository forms,
            UsersRepository users,
            IConfiguration config)
        {
            _forms = forms;
            _users = users;
            _config = config;
        }

        // ---------- Helpers ----------
        private static string? TryGetEmailFromClaims(ClaimsPrincipal user) =>
            user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("email")?.Value
            ?? user.FindFirst("upn")?.Value;

        private static string? TryGetBadgeFromClaims(ClaimsPrincipal user) =>
            user.FindFirst("badge")?.Value
            ?? user.FindFirst("employee_badge")?.Value
            ?? user.FindFirst("employeeId")?.Value
            ?? user.FindFirst("employeeid")?.Value;

        private async Task<bool> IsActorAdminAsync()
        {
            // Resolve by badge first, then by email
            var badge = TryGetBadgeFromClaims(User);
            UserRow? actor = null;

            if (!string.IsNullOrWhiteSpace(badge))
                actor = await _users.GetByBadgeAsync(badge!);

            if (actor is null)
            {
                var email = TryGetEmailFromClaims(User);
                if (!string.IsNullOrWhiteSpace(email))
                    actor = await _users.GetByEmailAsync(email!);
            }

            return (actor?.IsAdmin ?? 0) == 1;
        }

        // ---------- GET: /admin/stats/overview ----------
        /// <summary>Year overview for dashboard cards + monthly chart.</summary>
        [HttpGet("overview")]
        public async Task<IActionResult> Overview([FromQuery] int? year = null)
        {
            if (!await IsActorAdminAsync())
            {
                return Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden",
                    detail: "Admins only.");
            }

            var y = year ?? DateTime.UtcNow.Year;

            // Repo returns totals + 12 monthly buckets (1..12)
            var overview = await _forms.GetDashboardOverviewAsync(y);

            var completionRate = overview.TotalNew > 0
                ? (int)Math.Round(100.0 * overview.TotalCompleted / overview.TotalNew)
                : 0;

            // Ensure month keys are 1..12 in order
            var monthly = overview.Monthly.Select((m, idx) => new
            {
                month = idx + 1,
                // keep property names as the UI expects: "new" and "completed"
                @new = m.NewCount,
                completed = m.CompletedCount
            });

            var payload = new
            {
                year = y,
                totals = new
                {
                    totalNew = overview.TotalNew,
                    totalCompleted = overview.TotalCompleted,
                    outstanding = overview.Outstanding,
                    completionRate
                },
                monthly
            };

            return Ok(payload);
        }

        // ---------- GET: /admin/stats/recent ----------
        /// <summary>Recent submissions for the dashboard table/cards.</summary>
        [HttpGet("recent")]
        public async Task<IActionResult> Recent([FromQuery] int take = 10)
        {
            if (!await IsActorAdminAsync())
            {
                return Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden",
                    detail: "Admins only.");
            }

            if (take <= 0) take = 10;
            if (take > 50) take = 50; // safety cap

            var rows = await _forms.GetRecentAsync(take);

            var result = rows.Select(r => new
            {
                id = r.Id,
                title = string.IsNullOrWhiteSpace(r.Title) ? "Police report request" : r.Title!,
                owner = r.Submitter,
                status = r.Status,
                created = r.CreatedDate // UTC -> ISO 8601
            });

            return Ok(result);
        }

        // ---------- GET: /admin/stats/agencies ----------
        /// <summary>
        /// Top-N report counts grouped by reporting agency for a given year.
        /// Uses persisted computed column [reporting_agency_id] and joins to dbo.picklist_agency.
        /// Falls back to JSON name '$.reportingAgency' or 'Unknown' when no id/name is available.
        /// </summary>
        [HttpGet("agencies")]
        public async Task<IActionResult> Agencies([FromQuery] int? year = null, [FromQuery] int top = 12)
        {
            if (!await IsActorAdminAsync())
            {
                return Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden",
                    detail: "Admins only.");
            }

            var y = year ?? DateTime.UtcNow.Year;
            var fromUtc = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var toUtc = fromUtc.AddYears(1);
            if (top <= 0 || top > 1000) top = 12;

            const string sql = @"
SELECT TOP (@Top)
    s.agency_id AS Id,
    COALESCE(NULLIF(a.name, N''), NULLIF(s.json_name, N''), N'Unknown') AS [Name],
    COUNT(1) AS [Count]
FROM
(
    SELECT 
        f.reporting_agency_id AS agency_id,
        CONVERT(NVARCHAR(128), JSON_VALUE(f.SubmittedRequestData, '$.reportingAgency')) AS json_name
    FROM dbo.submitted_request_form AS f
    WHERE f.CreatedDate >= @FromUtc AND f.CreatedDate < @ToUtc
) AS s
LEFT JOIN dbo.picklist_agency AS a
    ON a.id = s.agency_id
GROUP BY s.agency_id, COALESCE(NULLIF(a.name, N''), NULLIF(s.json_name, N''), N'Unknown')
ORDER BY [Count] DESC, [Name] ASC;";

            await using var conn = new SqlConnection(_config.GetConnectionString("DefaultConnection"));
            var rows = await conn.QueryAsync(new
            {
                Id = default(int?),
                Name = string.Empty,
                Count = 0
            }.GetType(), sql, new { FromUtc = fromUtc, ToUtc = toUtc, Top = top });

            return Ok(rows);
        }
    }
}
