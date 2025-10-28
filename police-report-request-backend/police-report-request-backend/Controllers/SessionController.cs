// Controllers/SessionController.cs
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using police_report_request_backend.Auth;   // IBadgeSessionService
using police_report_request_backend.Data;

namespace police_report_request_backend.Controllers
{
    [ApiController]
    [Route("api/session")]
    [Authorize]
    public sealed class SessionController : ControllerBase
    {
        private readonly IBadgeSessionService _badge;
        private readonly UsersRepository _usersRepo;
        private readonly ILogger<SessionController> _log;

        public SessionController(IBadgeSessionService badge, UsersRepository usersRepo, ILogger<SessionController> log)
        {
            _badge = badge;
            _usersRepo = usersRepo;
            _log = log;
        }

        private static string? EmailFrom(ClaimsPrincipal user) =>
            user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("email")?.Value
            ?? user.FindFirst("upn")?.Value;

        public sealed class BadgeRequest { public string Badge { get; set; } = ""; }

        // Client posts badge (from Graph) to set/refresh the HttpOnly cookie
        [HttpPost("badge")]
        public async Task<IActionResult> SetBadge([FromBody] BadgeRequest body)
        {
            var email = EmailFrom(User);
            if (string.IsNullOrWhiteSpace(email)) return Unauthorized("Missing email in token.");
            if (body is null || string.IsNullOrWhiteSpace(body.Badge)) return BadRequest("Badge is required.");

            var badge = body.Badge.Trim();

            // If caller is in Users (admins only), prefer the DB badge
            var row = await _usersRepo.GetByEmailAsync(email);
            if (row is not null && !string.IsNullOrWhiteSpace(row.Badge))
                badge = row.Badge;

            _badge.SetBadge(HttpContext, email, badge);

            var hasCookie = Request.Cookies.ContainsKey(BadgeSessionService.CookieName);
            _log.LogInformation("SetBadge: cookie appended. hasCookieNow={HasCookie}", hasCookie);

            // Return JSON for easier debugging in dev
            return Ok(new { ok = true, email, badge, cookieName = BadgeSessionService.CookieName });
        }

        // Lightweight "me" for the SPA to hydrate UI state
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            var email = EmailFrom(User) ?? "";
            var badge = User.FindFirst("badge")?.Value
                        ?? _badge.TryGetBadge(HttpContext, email)
                        ?? "";

            var u = !string.IsNullOrWhiteSpace(badge) ? await _usersRepo.GetByBadgeAsync(badge) : null;

            return Ok(new
            {
                email,
                badge,
                isAdmin = (u?.IsAdmin ?? 0) == 1,
                displayName = u?.DisplayName
            });
        }

        [HttpDelete("badge")]
        public IActionResult ClearBadge()
        {
            _badge.ClearBadge(HttpContext);
            return NoContent();
        }
    }
}
