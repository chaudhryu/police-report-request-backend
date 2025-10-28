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

        // Display name from Users or the CreatedBy badge.
        public string Submitter { get; set; } = default!;

        // Alias for code that expects 'Owner'.
        public string Owner => Submitter;

        // UTC timestamp from DB.
        public DateTime CreatedDate { get; set; }

        // Alias for code that expects 'CreatedDateUtc'.
        public DateTime CreatedDateUtc => CreatedDate;

        public string Status { get; set; } = default!;

        /// <summary>
        /// Optional short title/summarizer for lists.
        /// Populated from JSON (streetCrossings -> incidentType) or left null.
        /// </summary>
        public string? Title { get; set; }
    }

    public sealed class SubmittedRequestDetails
    {
        public int Id { get; set; }
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

        public int Outstanding => Math.Max(TotalNew - TotalCompleted, 0);

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
        /// IMPORTANT: LastUpdatedBy is NULL on insert (admins set it later when they take action).
        /// Returns the new identity Id.
        /// </summary>
        public async Task<int> InsertAsync(string createdByBadge, JsonElement payload)
        {
            if (string.IsNullOrWhiteSpace(createdByBadge))
                throw new ArgumentException("createdByBadge is required.", nameof(createdByBadge));

            // Validate + normalize JSON so any ISJSON/CHECK constraints pass consistently.
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

            const string insertSql = @"
INSERT INTO dbo.submitted_request_form
    (CreatedBy, SubmittedRequestData, LastUpdatedBy)
OUTPUT INSERTED.Id
VALUES
    (@CreatedBy, @SubmittedRequestData, NULL);";

            await using var conn = Conn();
            return await conn.ExecuteScalarAsync<int>(
                insertSql,
                new
                {
                    CreatedBy = createdByBadge.Trim(),
                    SubmittedRequestData = json
                });
        }

        /// <summary>Returns only the JSON blob for a single row.</summary>
        public async Task<string?> GetJsonByIdAsync(int id)
        {
            const string sql = "SELECT SubmittedRequestData FROM dbo.submitted_request_form WHERE Id = @Id;";
            await using var conn = Conn();
            return await conn.ExecuteScalarAsync<string?>(sql, new { Id = id });
        }

        /// <summary>
        /// Lists submissions; if createdByBadge is null, returns all.
        /// Supports date range and status filters. Adds a friendly Title from JSON when available.
        /// </summary>
        public async Task<IReadOnlyList<SubmittedRequestListItem>> ListAsync(
            string? createdByBadge,
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
    COALESCE(
        NULLIF(JSON_VALUE(f.SubmittedRequestData, '$.streetCrossings'), ''),
        NULLIF(JSON_VALUE(f.SubmittedRequestData, '$.incidentType'), '')
    ) AS Title,
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
            var rows = await conn.QueryAsync<SubmittedRequestListItem>(
                sql,
                new
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

        /// <summary>
        /// Admin-only: update status and stamp LastUpdatedBy.
        /// This relies on FK(submitted_request_form.LastUpdatedBy -> Users.Badge),
        /// so only badges present in dbo.Users are valid here (i.e., admins).
        /// </summary>
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

        /// <summary>
        /// Updates the JSON payload for a submission and bumps LastUpdatedDate.
        /// Expects a valid JSON string (will be validated).
        /// </summary>
        public async Task<int> UpdateSubmittedRequestDataJsonAsync(int id, string submittedRequestDataJson)
        {
            if (string.IsNullOrWhiteSpace(submittedRequestDataJson))
                throw new ArgumentException("submittedRequestDataJson must be valid JSON and not empty.", nameof(submittedRequestDataJson));

            string normalized;
            try
            {
                using var doc = JsonDocument.Parse(submittedRequestDataJson);
                normalized = doc.RootElement.GetRawText();
            }
            catch (JsonException)
            {
                throw new ArgumentException("submittedRequestDataJson must be valid JSON.", nameof(submittedRequestDataJson));
            }

            const string sql = @"
UPDATE dbo.submitted_request_form
SET SubmittedRequestData = @json,
    LastUpdatedDate = SYSUTCDATETIME()
WHERE Id = @Id;";

            await using var conn = Conn();
            return await conn.ExecuteAsync(sql, new { Id = id, json = normalized });
        }

        /// <summary>
        /// Top-N most recent items (no filters). Adds Title from JSON when available.
        /// </summary>
        public async Task<IReadOnlyList<SubmittedRequestListItem>> GetRecentAsync(int take = 5)
        {
            const string sql = @"
SELECT TOP (@Take)
    f.Id,
    COALESCE(NULLIF(LTRIM(RTRIM(u.DisplayName)), ''), f.CreatedBy) AS Submitter,
    COALESCE(
        NULLIF(JSON_VALUE(f.SubmittedRequestData, '$.streetCrossings'), ''),
        NULLIF(JSON_VALUE(f.SubmittedRequestData, '$.incidentType'), '')
    ) AS Title,
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
        /// Aggregates for a calendar year:
        /// - NewCount bucketed by MONTH(CreatedDate)
        /// - CompletedCount bucketed by MONTH(LastUpdatedDate) for rows currently in a completed status
        /// Returns full 12-month series (1..12) even when months are empty.
        /// </summary>
        public async Task<DashboardOverview> GetDashboardOverviewAsync(int year)
        {
            var fromUtc = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var toUtc = fromUtc.AddYears(1);

            const string totalsSql = @"
SELECT
    TotalNew = COUNT(*),
    TotalCompleted = SUM(CASE WHEN f.Status IN (N'Completed', N'Closed', N'Approved') THEN 1 ELSE 0 END)
FROM dbo.submitted_request_form AS f
WHERE f.CreatedDate >= @FromUtc
  AND f.CreatedDate <  @ToUtc;";

            const string monthlySql = @"
;WITH Months AS
(
    SELECT CAST(DATEFROMPARTS(@Year, 1, 1) AS date) AS MonthStart
    UNION ALL
    SELECT DATEADD(MONTH, 1, MonthStart)
    FROM Months
    WHERE MonthStart < DATEFROMPARTS(@Year, 12, 1)
),
NewC AS
(
    SELECT
        CAST(DATEFROMPARTS(YEAR(f.CreatedDate), MONTH(f.CreatedDate), 1) AS date) AS MonthStart,
        NewCount = COUNT(*)
    FROM dbo.submitted_request_form AS f
    WHERE f.CreatedDate >= @FromUtc AND f.CreatedDate < @ToUtc
    GROUP BY DATEFROMPARTS(YEAR(f.CreatedDate), MONTH(f.CreatedDate), 1)
),
CompC AS
(
    SELECT
        CAST(DATEFROMPARTS(YEAR(f.LastUpdatedDate), MONTH(f.LastUpdatedDate), 1) AS date) AS MonthStart,
        CompletedCount = COUNT(*)
    FROM dbo.submitted_request_form AS f
    WHERE f.LastUpdatedDate >= @FromUtc AND f.LastUpdatedDate < @ToUtc
      AND f.Status IN (N'Completed', N'Closed', N'Approved')
    GROUP BY DATEFROMPARTS(YEAR(f.LastUpdatedDate), MONTH(f.LastUpdatedDate), 1)
)
SELECT
    [Month]        = MONTH(m.MonthStart),   -- 1..12
    NewCount       = ISNULL(n.NewCount, 0),
    CompletedCount = ISNULL(c.CompletedCount, 0)
FROM Months m
LEFT JOIN NewC  n ON n.MonthStart = m.MonthStart
LEFT JOIN CompC c ON c.MonthStart = m.MonthStart
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
                Year = year,
                TotalNew = totals.TotalNew,
                TotalCompleted = totals.TotalCompleted,
                Monthly = monthly
            };
        }
    }
}
