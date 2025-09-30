// Data/SubmittedRequestFormRepository.cs
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace police_report_request_backend.Data
{
    public sealed class SubmittedRequestListItem
    {
        public int Id { get; set; }

        /// <summary>Display name from Users or the CreatedBy badge.</summary>
        public string Submitter { get; set; } = default!;

        /// <summary>Alias for code that expects 'Owner'.</summary>
        public string Owner => Submitter;

        /// <summary>UTC timestamp from DB.</summary>
        public DateTime CreatedDate { get; set; }

        /// <summary>Alias for code that expects 'CreatedDateUtc'.</summary>
        public DateTime CreatedDateUtc => CreatedDate;

        public string Status { get; set; } = default!;

        /// <summary>
        /// Optional title/summarizer for lists. Populated from JSON: $.incidentType;
        /// falls back to "Police Report Request".
        /// </summary>
        public string? Title { get; set; }
    }

    public sealed class SubmittedRequestDetails
    {
        public int Id { get; set; }
        public int RequestFormId { get; set; }
        public string CreatedBy { get; set; } = default!;
        public string? CreatedByDisplayName { get; set; }
        public string Status { get; set; } = default!;
        public DateTime CreatedDate { get; set; }
        public DateTime LastUpdatedDate { get; set; }
        public string SubmittedRequestDataJson { get; set; } = default!;
    }

    /// <summary>Simple aggregates for an admin dashboard.</summary>
    public sealed class DashboardOverview
    {
        public int Year { get; set; }
        public int TotalNew { get; set; }
        public int TotalCompleted { get; set; }

        /// <summary>TotalNew - TotalCompleted, never below 0.</summary>
        public int Outstanding => Math.Max(TotalNew - TotalCompleted, 0);

        /// <summary>Rounded percentage of completed over new.</summary>
        public int CompletionRate => TotalNew <= 0
            ? 0
            : (int)Math.Round(TotalCompleted * 100.0 / TotalNew);

        public IReadOnlyList<MonthlyBucket> Monthly { get; set; } = Array.Empty<MonthlyBucket>();
    }

    public sealed class MonthlyBucket
    {
        /// <summary>Month number 1..12.</summary>
        public int Month { get; set; }
        public int NewCount { get; set; }
        public int CompletedCount { get; set; }
    }

    public sealed class SubmittedRequestFormRepository
    {
        private readonly string _connStr;

        public SubmittedRequestFormRepository(IConfiguration cfg)
        {
            _connStr = cfg.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is missing.");
        }

        private SqlConnection Conn() => new SqlConnection(_connStr);

        /// <summary>
        /// Inserts a submission row. Lets DB defaults populate Status/CreatedDate/LastUpdatedDate.
        /// Returns the new identity Id.
        /// </summary>
        public async Task<int> InsertAsync(int requestFormId, string createdByBadge, JsonElement payload)
        {
            // Validate + normalize JSON to ensure your CHECK (ISJSON(...)=1) passes
            string json;
            try
            {
                using var doc = JsonDocument.Parse(payload.GetRawText());
                json = doc.RootElement.GetRawText();
            }
            catch (JsonException)
            {
                throw new ArgumentException("SubmittedRequestData must be valid JSON.");
            }

            const string sql = @"
INSERT INTO dbo.submitted_request_form
    (RequestFormId, CreatedBy, SubmittedRequestData, LastUpdatedBy)
OUTPUT INSERTED.Id
VALUES
    (@RequestFormId, @CreatedBy, @SubmittedRequestData, @CreatedBy);";

            await using var conn = Conn();
            try
            {
                return await conn.ExecuteScalarAsync<int>(sql, new
                {
                    RequestFormId = requestFormId,
                    CreatedBy = createdByBadge,   // must exist in dbo.Users(Badge)
                    SubmittedRequestData = json
                });
            }
            catch (SqlException ex) when (ex.Number == 547) // FK violation
            {
                throw new InvalidOperationException($"CreatedBy badge '{createdByBadge}' is not a valid user.", ex);
            }
        }

        /// <summary>
        /// Returns just the JSON blob for a single row (useful for a details view).
        /// </summary>
        public async Task<string?> GetJsonByIdAsync(int id)
        {
            const string sql = "SELECT SubmittedRequestData FROM dbo.submitted_request_form WHERE Id = @Id;";
            await using var conn = Conn();
            return await conn.ExecuteScalarAsync<string?>(sql, new { Id = id });
        }

        /// <summary>
        /// Lists submissions; if <paramref name=""createdByBadge""/> is null, returns all.
        /// Supports date range and status filters. Also projects a short Title from JSON.
        /// </summary>
        public async Task<IReadOnlyList<SubmittedRequestListItem>> ListAsync(
            string? createdByBadge,    // null => include all
            DateTime? fromUtc,
            DateTime? toUtc,
            string? status,
            int skip = 0,
            int take = 500)
        {
            const string sql = @"
SELECT
    f.Id,
    COALESCE(NULLIF(LTRIM(RTRIM(u.DisplayName)), ''), f.CreatedBy) AS Submitter,
    COALESCE(NULLIF(JSON_VALUE(f.SubmittedRequestData, '$.incidentType'), ''), N'Police Report Request') AS Title,
    f.CreatedDate,
    f.Status
FROM dbo.submitted_request_form AS f
LEFT JOIN dbo.Users u ON u.Badge = f.CreatedBy
WHERE
    (@CreatedBy IS NULL OR f.CreatedBy = @CreatedBy)
    AND (@Status    IS NULL OR f.Status = @Status)
    AND (@FromUtc   IS NULL OR f.CreatedDate >= @FromUtc)
    AND (@ToUtc     IS NULL OR f.CreatedDate <  @ToUtc)
ORDER BY f.CreatedDate DESC
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;";

            await using var conn = Conn();
            var rows = await conn.QueryAsync<SubmittedRequestListItem>(sql, new
            {
                CreatedBy = createdByBadge,
                Status = string.IsNullOrWhiteSpace(status) ? null : status,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Skip = Math.Max(0, skip),
                Take = take <= 0 ? 500 : take
            });

            return rows.AsList();
        }

        public async Task<SubmittedRequestDetails?> GetDetailsAsync(int id)
        {
            const string sql = @"
SELECT
    f.Id,
    f.RequestFormId,
    f.CreatedBy,
    COALESCE(NULLIF(LTRIM(RTRIM(u.DisplayName)), ''), f.CreatedBy) AS CreatedByDisplayName,
    f.Status,
    f.CreatedDate,
    f.LastUpdatedDate,
    f.SubmittedRequestData AS SubmittedRequestDataJson
FROM dbo.submitted_request_form f
LEFT JOIN dbo.Users u ON u.Badge = f.CreatedBy
WHERE f.Id = @Id;";
            await using var conn = Conn();
            return await conn.QuerySingleOrDefaultAsync<SubmittedRequestDetails>(sql, new { Id = id });
        }

        public async Task<int> UpdateStatusAsync(int id, string newStatus, string actorBadge)
        {
            const string sql = @"
UPDATE dbo.submitted_request_form
SET Status = @Status,
    LastUpdatedBy = @Actor,
    LastUpdatedDate = SYSUTCDATETIME()
WHERE Id = @Id;";
            await using var conn = Conn();
            return await conn.ExecuteAsync(sql, new { Id = id, Status = newStatus, Actor = actorBadge });
        }

        // ------------------------------
        // Dashboard helpers
        // ------------------------------

        /// <summary>Top-N most recent items (no filters).</summary>
        public async Task<IReadOnlyList<SubmittedRequestListItem>> GetRecentAsync(int take = 5)
        {
            const string sql = @"
SELECT TOP (@Take)
    f.Id,
    COALESCE(NULLIF(LTRIM(RTRIM(u.DisplayName)), ''), f.CreatedBy) AS Submitter,
    COALESCE(NULLIF(JSON_VALUE(f.SubmittedRequestData, '$.incidentType'), ''), N'Police Report Request') AS Title,
    f.CreatedDate,
    f.Status
FROM dbo.submitted_request_form f
LEFT JOIN dbo.Users u ON u.Badge = f.CreatedBy
ORDER BY f.CreatedDate DESC;";
            await using var conn = Conn();
            var rows = await conn.QueryAsync<SubmittedRequestListItem>(sql, new { Take = take <= 0 ? 5 : take });
            return rows.AsList();
        }

        /// <summary>
        /// Aggregates for a given year:
        /// - NewCount: bucketed by MONTH(CreatedDate)
        /// - CompletedCount: bucketed by MONTH(LastUpdatedDate) where Status IN ('Completed','Closed')
        /// Returns full 12-month series (1..12) even when months are empty.
        /// </summary>
        public async Task<DashboardOverview> GetDashboardOverviewAsync(int year)
        {
            // Build [start, end) UTC window for that calendar year
            var fromUtc = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var toUtc = fromUtc.AddYears(1);

            const string totalsSql = @"
SELECT
    TotalNew = COUNT(*),
    TotalCompleted = SUM(CASE WHEN f.Status IN (N'Completed', N'Closed', N'Approved') THEN 1 ELSE 0 END)
FROM dbo.submitted_request_form AS f
WHERE f.CreatedDate >= @FromUtc
  AND f.CreatedDate <  @ToUtc;";

            // Generate 12 month starts for the year, then left-join aggregated facts
            const string monthlySql = @"
;WITH Months AS
(
    SELECT CAST(DATEFROMPARTS(@Year, 1, 1) AS date) AS MonthStart
    UNION ALL
    SELECT DATEADD(MONTH, 1, MonthStart)
    FROM Months
    WHERE MonthStart < DATEFROMPARTS(@Year, 12, 1)
),
Agg AS
(
    SELECT
        CAST(DATEFROMPARTS(YEAR(f.CreatedDate), MONTH(f.CreatedDate), 1) AS date) AS MonthStart,
        NewCount       = COUNT(*) ,
        CompletedCount = SUM(CASE WHEN f.Status IN (N'Completed', N'Closed', N'Approved') THEN 1 ELSE 0 END)
    FROM dbo.submitted_request_form AS f
    WHERE f.CreatedDate >= @FromUtc
      AND f.CreatedDate <  @ToUtc
    GROUP BY DATEFROMPARTS(YEAR(f.CreatedDate), MONTH(f.CreatedDate), 1)
)
SELECT
    MonthStartUtc  = CAST(m.MonthStart AS datetime2(0)),
    NewCount       = ISNULL(a.NewCount, 0),
    CompletedCount = ISNULL(a.CompletedCount, 0)
FROM Months AS m
LEFT JOIN Agg AS a
    ON a.MonthStart = m.MonthStart
ORDER BY m.MonthStart
OPTION (MAXRECURSION 12);";

            await using var conn = Conn();

            var totals = await conn.QuerySingleAsync<(int TotalNew, int TotalCompleted)>(
                totalsSql,
                new { FromUtc = fromUtc, ToUtc = toUtc });

            var monthly = (await conn.QueryAsync<MonthlyBucket>(
                monthlySql,
                new { Year = year, FromUtc = fromUtc, ToUtc = toUtc }))
                .AsList();

            return new DashboardOverview
            {
                TotalNew = totals.TotalNew,
                TotalCompleted = totals.TotalCompleted,
                Monthly = monthly
            };
        }

    }
}
