using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace police_report_request_backend.Controllers
{
    [ApiController]
    [Route("[controller]")] // <— removed "api/"
    public sealed class PicklistsController : ControllerBase
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<PicklistsController> _log;

        public PicklistsController(IConfiguration cfg, ILogger<PicklistsController> log)
        {
            _cfg = cfg;
            _log = log;
        }

        private SqlConnection Conn()
        {
            var cs = _cfg.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException("ConnectionStrings:DefaultConnection is missing.");
            return new SqlConnection(cs);
        }

        public sealed record PickItem(int Id, string Name);

        // GET /picklists/reporting-agencies
        [HttpGet("reporting-agencies")]
        public async Task<IActionResult> GetReportingAgencies()
        {
            const string sql = @"
SELECT id AS Id, name AS Name
FROM dbo.picklist_agency
ORDER BY name ASC;";

            try
            {
                await using var conn = Conn();
                var rows = await conn.QueryAsync<PickItem>(sql);
                return Ok(rows);
            }
            catch (SqlException ex)
            {
                _log.LogError(ex, "Failed to load reporting agencies from dbo.picklist_agency.");
                return Problem(statusCode: 500, title: "Database error", detail: ex.Message);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unexpected error loading reporting agencies.");
                return Problem(statusCode: 500, title: "Server error", detail: ex.Message);
            }
        }
    }
}
