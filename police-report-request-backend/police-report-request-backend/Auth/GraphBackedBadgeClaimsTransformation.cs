// Auth/GraphBackedBadgeClaimsTransformation.cs
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Abstractions;         // <-- IDownstreamApi
using police_report_request_backend.Data;

namespace police_report_request_backend.Auth;

public sealed class GraphBackedBadgeClaimsTransformation : IClaimsTransformation
{
    private readonly IDownstreamApi _graph;    // <-- new interface
    private readonly UsersRepository _users;
    private readonly ILogger<GraphBackedBadgeClaimsTransformation> _log;

    public GraphBackedBadgeClaimsTransformation(
        IDownstreamApi graph,                   // <-- inject IDownstreamApi
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

        // If badge already present, nothing to do
        if (id.FindFirst("badge") is not null)
            return principal;

        // 1) Try to map via local Users table using email/UPN from token
        var email = TryGetEmailFromClaims(principal);
        if (!string.IsNullOrWhiteSpace(email))
        {
            var userRow = await _users.GetByEmailAsync(email);
            if (!string.IsNullOrWhiteSpace(userRow?.Badge))
            {
                id.AddClaim(new Claim("badge", userRow!.Badge));
                _log.LogInformation("ClaimsXform: mapped email/UPN {Email} -> badge {Badge} via Users.", email, userRow.Badge);
                return principal;
            }
        }

        // 2) As a fallback, ask Graph for /me and try to pull a badge-like value
        try
        {
            var resp = await _graph.CallApiForUserAsync(
                "Graph",
                opts =>
                {
                    // Ask only for the small set of fields we might use
                    opts.RelativePath = "me?$select=officeLocation,mail,userPrincipalName,displayName";
                },
                user: principal);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("ClaimsXform: Graph /me returned HTTP {Status}.", (int)resp.StatusCode);
                return principal;
            }

            var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            var root = doc?.RootElement ?? default;

            static string? GetStr(JsonElement e, string name)
                => e.ValueKind == JsonValueKind.Undefined ? null
                 : e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;

            var officeLocation = GetStr(root, "officeLocation");     // sometimes used internally for a badge
            var graphMail = GetStr(root, "mail");
            var graphUpn = GetStr(root, "userPrincipalName");

            // If officeLocation looks like a 5-digit badge, use it
            if (!string.IsNullOrWhiteSpace(officeLocation) && IsFiveDigits(officeLocation))
            {
                id.AddClaim(new Claim("badge", officeLocation!));
                _log.LogInformation("ClaimsXform: badge={Badge} via Graph officeLocation.", officeLocation);
                return principal;
            }

            // Otherwise try our Users table again with Graph's mail/UPN (belt & suspenders)
            var altEmail = graphMail ?? graphUpn;
            if (!string.IsNullOrWhiteSpace(altEmail))
            {
                var userRow = await _users.GetByEmailAsync(altEmail!);
                if (!string.IsNullOrWhiteSpace(userRow?.Badge))
                {
                    id.AddClaim(new Claim("badge", userRow!.Badge));
                    _log.LogInformation("ClaimsXform: mapped Graph identity {AltEmail} -> badge {Badge} via Users.", altEmail, userRow.Badge);
                    return principal;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "ClaimsXform: Graph OBO call failed.");
        }

        // Still no badge — leave as-is; downstream endpoints will 403 if they require it
        return principal;
    }

    private static string? TryGetEmailFromClaims(ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value
        ?? user.FindFirst(ClaimTypes.Email)?.Value
        ?? user.FindFirst("email")?.Value
        ?? user.FindFirst("upn")?.Value;

    private static bool IsFiveDigits(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Trim().Length == 5 && s.All(char.IsDigit);
}
