using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Identity.Web;
using police_report_request_backend.Data;

namespace police_report_request_backend.Auth
{
    internal sealed class GraphBackedBadgeClaimsTransformation : IClaimsTransformation
    {
        private readonly UsersRepository _usersRepo;
        private readonly IDownstreamWebApi _graph;
        private readonly ILogger<GraphBackedBadgeClaimsTransformation> _log;

        public GraphBackedBadgeClaimsTransformation(
            UsersRepository usersRepo,
            IDownstreamWebApi graph,
            ILogger<GraphBackedBadgeClaimsTransformation> log)
        {
            _usersRepo = usersRepo;
            _graph = graph;
            _log = log;
        }

        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            if (principal.Identity?.IsAuthenticated != true) return principal;

            if (principal.FindFirst("badge") is not null)
            {
                _log.LogInformation("ClaimsXform: badge already present.");
                return principal;
            }

            var id = principal.Identity as ClaimsIdentity;
            if (id is null)
            {
                _log.LogInformation("ClaimsXform: no ClaimsIdentity; cannot add badge.");
                return principal;
            }

            // 1) Try Users by any email-like claim
            string? emailish =
                principal.FindFirst("preferred_username")?.Value
                ?? principal.FindFirst(ClaimTypes.Email)?.Value
                ?? principal.FindFirst("email")?.Value
                ?? principal.FindFirst("upn")?.Value
                ?? principal.FindFirst("unique_name")?.Value;

            if (!string.IsNullOrWhiteSpace(emailish))
            {
                var u = await _usersRepo.GetByEmailAsync(emailish);
                if (!string.IsNullOrWhiteSpace(u?.Badge))
                {
                    id.AddClaim(new Claim("badge", u.Badge));
                    _log.LogInformation("ClaimsXform: mapped email/UPN {Emailish} -> badge {Badge} via Users.", emailish, u.Badge);
                    return principal;
                }
            }

            // 2) Call Graph OBO with the CURRENT principal to read mail/upn/officeLocation
            try
            {
                var resp = await _graph.CallWebApiForUserAsync(
                    "Graph",
                    opts => { opts.RelativePath = "me?$select=mail,userPrincipalName,officeLocation"; },
                    principal  // <-- pass the same principal we are transforming
                );

                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogInformation("ClaimsXform: Graph /me failed. Status={Status}", resp.StatusCode);
                    return principal;
                }

                var doc = (await resp.Content.ReadFromJsonAsync<JsonDocument>()) ?? JsonDocument.Parse("{}");
                var root = doc.RootElement;

                var mail = root.TryGetProperty("mail", out var m) ? m.GetString() : null;
                var upn = root.TryGetProperty("userPrincipalName", out var uprop) ? uprop.GetString() : null;
                var office = root.TryGetProperty("officeLocation", out var o) ? o.GetString() : null;

                if (!string.IsNullOrWhiteSpace(office))
                {
                    id.AddClaim(new Claim("badge", office));
                    _log.LogInformation("ClaimsXform: added badge {Badge} from Graph officeLocation.", office);
                    return principal;
                }

                var emailFromGraph = !string.IsNullOrWhiteSpace(mail) ? mail : upn;
                if (!string.IsNullOrWhiteSpace(emailFromGraph))
                {
                    var u = await _usersRepo.GetByEmailAsync(emailFromGraph);
                    if (!string.IsNullOrWhiteSpace(u?.Badge))
                    {
                        id.AddClaim(new Claim("badge", u.Badge));
                        _log.LogInformation("ClaimsXform: inferred badge {Badge} via Graph email/UPN {Email}.", u.Badge, emailFromGraph);
                        return principal;
                    }
                }

                _log.LogInformation("ClaimsXform: unable to infer badge via Graph (mail={Mail}, upn={Upn}, officeLocation={Office}).",
                    mail, upn, office);
            }
            catch (Exception ex)
            {
                _log.LogInformation(ex, "ClaimsXform: Graph OBO call failed.");
            }

            return principal;
        }
    }
}
