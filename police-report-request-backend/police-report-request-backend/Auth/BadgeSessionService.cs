// Auth/BadgeSessionService.cs
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace police_report_request_backend.Auth
{
    public interface IBadgeSessionService
    {
        /// <summary>
        /// Set (or refresh) the server-signed, HttpOnly badge session cookie for the current user.
        /// The cookie value is protected with DataProtection and contains { email, badge, exp }.
        /// </summary>
        void SetBadge(HttpContext ctx, string email, string badge, TimeSpan? lifetime = null);

        /// <summary>
        /// Try to read and validate the badge from the HttpOnly cookie.
        /// Validates signature, expiry, and that the cookie's email matches the token's email.
        /// Returns null on any validation failure.
        /// </summary>
        string? TryGetBadge(HttpContext ctx, string email);

        /// <summary>
        /// Deletes the badge cookie by setting an expired cookie.
        /// </summary>
        void ClearBadge(HttpContext ctx);
    }

    /// <summary>
    /// Stores a protected { email, badge, exp } in an HttpOnly cookie.
    /// Cookie is later read to add a 'badge' claim or for controllers to use.
    /// </summary>
    public sealed class BadgeSessionService : IBadgeSessionService
    {
        public const string CookieName = "prrp_badge";

        private readonly IDataProtector _protector;
        private readonly IHostEnvironment _env;
        private readonly ILogger<BadgeSessionService> _log;

        private sealed record Payload(string e, string b, DateTimeOffset exp);

        public BadgeSessionService(
            IDataProtectionProvider dataProtectionProvider,
            IHostEnvironment env,
            ILogger<BadgeSessionService> log)
        {
            _protector = dataProtectionProvider.CreateProtector("prrp-badge-cookie.v1");
            _env = env;
            _log = log;
        }

        public void SetBadge(HttpContext ctx, string email, string badge, TimeSpan? lifetime = null)
        {
            if (ctx is null) throw new ArgumentNullException(nameof(ctx));
            if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("email required", nameof(email));
            if (string.IsNullOrWhiteSpace(badge)) throw new ArgumentException("badge required", nameof(badge));

            var ttl = lifetime ?? TimeSpan.FromDays(7);
            var payload = new Payload(email.ToLowerInvariant().Trim(), badge.Trim(), DateTimeOffset.UtcNow.Add(ttl));
            var json = JsonSerializer.Serialize(payload);
            var protectedValue = _protector.Protect(json);

            // Always set SameSite=None; Secure=true so cookie can travel cross-site over HTTPS
            var opts = new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                IsEssential = true,
                Expires = payload.exp,
                Path = "/"
                // Domain: omit so the browser uses current host. Set only if you need a parent domain cookie.
            };

            ctx.Response.Cookies.Append(CookieName, protectedValue, opts);
            _log.LogInformation("Badge cookie SET for {Email} (ttl={TtlDays}d). IsHttps={IsHttps}", payload.e, ttl.TotalDays, ctx.Request.IsHttps);
        }

        public string? TryGetBadge(HttpContext ctx, string email)
        {
            if (ctx is null) return null;
            if (string.IsNullOrWhiteSpace(email)) return null;

            if (!ctx.Request.Cookies.TryGetValue(CookieName, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                _log.LogInformation("Badge cookie NOT PRESENT on request. Email={Email}", email);
                return null;
            }

            try
            {
                var json = _protector.Unprotect(raw);
                var p = JsonSerializer.Deserialize<Payload>(json);
                if (p is null)
                {
                    _log.LogInformation("Badge cookie: payload null after unprotect.");
                    return null;
                }

                if (p.exp <= DateTimeOffset.UtcNow)
                {
                    _log.LogInformation("Badge cookie EXPIRED at {Exp:o}.", p.exp);
                    return null;
                }

                if (!string.Equals(p.e, email.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("Badge cookie EMAIL MISMATCH. Cookie={CookieEmail}, Token={TokenEmail}", p.e, email);
                    return null;
                }

                _log.LogInformation("Badge cookie READ OK. Email={Email}, Badge={Badge}", email, p.b);
                return p.b;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Badge cookie unprotect/parse failed.");
                return null;
            }
        }

        public void ClearBadge(HttpContext ctx)
        {
            if (ctx is null) return;

            var opts = new CookieOptions
            {
                Expires = DateTimeOffset.UnixEpoch,
                Path = "/"
            };

            ctx.Response.Cookies.Delete(CookieName, opts);
            _log.LogInformation("Badge cookie CLEARED.");
        }
    }
}
