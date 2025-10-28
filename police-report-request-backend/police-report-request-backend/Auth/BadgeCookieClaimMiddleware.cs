// Auth/BadgeCookieClaimMiddleware.cs
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace police_report_request_backend.Auth
{
    /// <summary>
    /// Guarantees that authenticated users have a 'badge' claim by reading
    /// the server-signed HttpOnly cookie (set by POST /api/session/badge).
    /// Runs on EVERY authenticated request, before authorization.
    /// </summary>
    public sealed class BadgeCookieClaimMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<BadgeCookieClaimMiddleware> _log;

        public BadgeCookieClaimMiddleware(RequestDelegate next, ILogger<BadgeCookieClaimMiddleware> log)
        {
            _next = next;
            _log = log;
        }

        public async Task InvokeAsync(HttpContext ctx, IBadgeSessionService badgeSvc)
        {
            var id = ctx.User?.Identity as ClaimsIdentity;

            if (id?.IsAuthenticated == true && id.FindFirst("badge") is null)
            {
                // Pull an email-like identifier from the access token (API token)
                var email = id.FindFirst("preferred_username")?.Value
                            ?? id.FindFirst(ClaimTypes.Email)?.Value
                            ?? id.FindFirst("email")?.Value
                            ?? id.FindFirst("upn")?.Value;

                if (!string.IsNullOrWhiteSpace(email))
                {
                    var badge = badgeSvc.TryGetBadge(ctx, email);
                    if (!string.IsNullOrWhiteSpace(badge))
                    {
                        id.AddClaim(new Claim("badge", badge));
                        _log.LogInformation("BadgeCookieMW: Injected 'badge' claim ({Badge}) for {Email}.", badge, email);
                    }
                    else
                    {
                        _log.LogDebug("BadgeCookieMW: Cookie absent/invalid for {Email}.", email);
                    }
                }
                else
                {
                    _log.LogDebug("BadgeCookieMW: No email-like claim found on principal; cannot bind cookie.");
                }
            }

            await _next(ctx);
        }
    }
}
