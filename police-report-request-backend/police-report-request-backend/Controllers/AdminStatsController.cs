// Controllers/AdminStatsController.cs
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using police_report_request_backend.Data;

namespace police_report_request_backend.Controllers
{
    [ApiController]
    [Route("api/admin/stats")]
    [Authorize] // we'll still verify admin in-code
    [Produces("application/json")]
    public sealed class AdminStatsController : ControllerBase
    {
        private readonly SubmittedRequestFormRepository _forms;
        private readonly UsersRepository _users;

        public AdminStatsController(SubmittedRequestFormRepository forms, UsersRepository users)
        {
            _forms = forms;
            _users = users;
        }

        /// <summary>
        /// Year overview for dashboard cards + monthly chart.
        /// </summary>
        [HttpGet("overview")]
        public async Task<IActionResult> Overview([FromQuery] int? year = null)
        {
            // Server-side admin check
            var actorEmail = TryGetEmailFromClaims(User);
            var actor = string.IsNullOrWhiteSpace(actorEmail)
                ? null
                : await _users.GetByEmailAsync(actorEmail);

            var isAdmin = (actor?.IsAdmin ?? 0) == 1;
            if (!isAdmin)
            {
                return Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden",
                    detail: "Admins only.");
            }

            var y = year ?? DateTime.UtcNow.Year;

            // Repo overload expects YEAR (not a date range)
            var overview = await _forms.GetDashboardOverviewAsync(y);

            // Compute completion rate defensively here to avoid model name mismatches.
            var completionRate = overview.TotalNew > 0
                ? (int)Math.Round(100.0 * overview.TotalCompleted / overview.TotalNew)
                : 0;

            // Project monthly as 1..12 in the order returned by the repo.
            var monthly = overview.Monthly.Select((m, idx) => new
            {
                month = idx + 1,   // 1..12 (client labels)
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

        /// <summary>
        /// Recent submissions for the dashboard table/cards.
        /// </summary>
        [HttpGet("recent")]
        public async Task<IActionResult> Recent([FromQuery] int take = 10)
        {
            // Server-side admin check
            var actorEmail = TryGetEmailFromClaims(User);
            var actor = string.IsNullOrWhiteSpace(actorEmail)
                ? null
                : await _users.GetByEmailAsync(actorEmail);

            var isAdmin = (actor?.IsAdmin ?? 0) == 1;
            if (!isAdmin)
            {
                return Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden",
                    detail: "Admins only.");
            }

            if (take <= 0) take = 10;
            if (take > 50) take = 50; // simple safety cap

            var rows = await _forms.GetRecentAsync(take);

            // Use Title when available; otherwise fall back to a generic label
            var result = rows.Select(r => new
            {
                id = r.Id,
                title = string.IsNullOrWhiteSpace(r.Title) ? "Police report request" : r.Title!,
                owner = r.Submitter,
                status = r.Status,
                created = r.CreatedDate // DateTime (UTC) will serialize to ISO 8601
            });

            return Ok(result);
        }

        // ---- helpers ----
        private static string? TryGetEmailFromClaims(ClaimsPrincipal user) =>
            user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("email")?.Value
            ?? user.FindFirst("upn")?.Value;
    }
}
