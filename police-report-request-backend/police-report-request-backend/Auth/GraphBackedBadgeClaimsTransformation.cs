// Auth/GraphBackedBadgeClaimsTransformation.cs
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Abstractions;         // IDownstreamApi
using police_report_request_backend.Data;

namespace police_report_request_backend.Auth;

public sealed class GraphBackedBadgeClaimsTransformation : IClaimsTransformation
{
    private readonly IDownstreamApi _graph;    // Downstream Graph caller via OBO
    private readonly UsersRepository _users;   // Admins-only table
    private readonly ILogger<GraphBackedBadgeClaimsTransformation> _log;

    // Badge normalization settings (adjust if needed)
    private const int MinBadgeDigits = 3;
    private const int MaxBadgeDigits = 10;

    public GraphBackedBadgeClaimsTransformation(
        IDownstreamApi graph,
        UsersRepository users,
        ILogger<GraphBackedBadgeClaimsTransformation> log)
    {
        _graph = graph;
        _users = users;
        _log = log;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal?.Identity is not ClaimsIdentity id || !id.IsAuthenticated)
            return principal;

        // 0) If Program.cs already injected 'badge' from the HttpOnly cookie, we're done.
        if (id.FindFirst("badge") is not null)
            return principal;

        // 1) Try to map via Users table using email/UPN from token (admins only).
        var email = TryGetEmailFromClaims(principal);
        if (!string.IsNullOrWhiteSpace(email))
        {
            var userRow = await _users.GetByEmailAsync(email);
            if (!string.IsNullOrWhiteSpace(userRow?.Badge))
            {
                id.AddClaim(new Claim("badge", userRow.Badge));
                _log.LogInformation("ClaimsXform: mapped email {Email} -> badge {Badge} via Users.", email, userRow.Badge);
                return principal;
            }
        }

        // 2) As a *last* resort, call Graph /me to infer a badge-like value.
        //    This path should be rare now that the cookie is the norm.
        try
        {
            var resp = await _graph.CallApiForUserAsync(
                "Graph",
                opts => { opts.RelativePath = "me?$select=officeLocation,mail,userPrincipalName,displayName"; },
                user: principal);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("ClaimsXform: Graph /me returned HTTP {Status}. Skipping.", (int)resp.StatusCode);
                return principal;
            }

            var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            var root = doc?.RootElement ?? default;

            string? OfficeLocation(JsonElement e)
                => e.ValueKind != JsonValueKind.Undefined && e.TryGetProperty("officeLocation", out var p)
                    && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            string? Mail(JsonElement e)
                => e.ValueKind != JsonValueKind.Undefined && e.TryGetProperty("mail", out var p)
                    && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            string? Upn(JsonElement e)
                => e.ValueKind != JsonValueKind.Undefined && e.TryGetProperty("userPrincipalName", out var p)
                    && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            var officeLocation = OfficeLocation(root);
            var graphMail = Mail(root);
            var graphUpn = Upn(root);

            // If officeLocation looks like a numeric badge in our accepted range, use it.
            if (LooksLikeBadge(officeLocation))
            {
                id.AddClaim(new Claim("badge", officeLocation!.Trim()));
                _log.LogInformation("ClaimsXform: badge={Badge} via Graph officeLocation.", officeLocation);
                return principal;
            }

            // Otherwise try our Users table again with Graph-provided mail/UPN.
            var altEmail = graphMail ?? graphUpn;
            if (!string.IsNullOrWhiteSpace(altEmail))
            {
                var userRow = await _users.GetByEmailAsync(altEmail!);
                if (!string.IsNullOrWhiteSpace(userRow?.Badge))
                {
                    id.AddClaim(new Claim("badge", userRow.Badge));
                    _log.LogInformation("ClaimsXform: mapped Graph identity {AltEmail} -> badge {Badge} via Users.", altEmail, userRow.Badge);
                    return principal;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "ClaimsXform: Graph OBO call failed; leaving principal unchanged.");
        }

        // 3) Still no badge — leave as-is; downstream endpoints that require a badge will 403.
        return principal;
    }

    private static string? TryGetEmailFromClaims(ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value
        ?? user.FindFirst(ClaimTypes.Email)?.Value
        ?? user.FindFirst("email")?.Value
        ?? user.FindFirst("upn")?.Value;

    private static bool LooksLikeBadge(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.Trim();
        if (trimmed.Length < MinBadgeDigits || trimmed.Length > MaxBadgeDigits) return false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (!char.IsDigit(trimmed[i])) return false;
        }
        return true;
    }
}
