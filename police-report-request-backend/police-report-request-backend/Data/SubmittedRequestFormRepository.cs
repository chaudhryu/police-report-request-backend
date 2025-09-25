using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace police_report_request_backend.Data;

public sealed class SubmittedRequestListItem
{
    public int Id { get; set; }
    public string Submitter { get; set; } = default!;  // DisplayName from Users or CreatedBy badge
    public DateTime CreatedDate { get; set; }          // UTC from DB
    public string Status { get; set; } = default!;
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
    /// Lists submissions; if <paramref name="createdByBadge"/> is null, returns all.
    /// Supports simple date range and status filters.
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
    f.CreatedDate,
    f.Status
FROM dbo.submitted_request_form AS f
LEFT JOIN dbo.Users u ON u.Badge = f.CreatedBy
WHERE
    (@CreatedBy IS NULL OR f.CreatedBy = @CreatedBy)
    AND (@Status IS NULL OR f.Status = @Status)
    AND (@FromUtc IS NULL OR f.CreatedDate >= @FromUtc)
    AND (@ToUtc IS NULL OR f.CreatedDate < @ToUtc)
ORDER BY f.CreatedDate DESC
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;";

        await using var conn = Conn();
        var rows = await conn.QueryAsync<SubmittedRequestListItem>(sql, new
        {
            CreatedBy = createdByBadge,
            Status = string.IsNullOrWhiteSpace(status) ? null : status,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Skip = skip < 0 ? 0 : skip,
            Take = take <= 0 ? 500 : take
        });

        return rows.AsList();
    }
    // Data/SubmittedRequestFormRepository.cs (inside the class)

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
        await using var conn = new SqlConnection(_connStr);
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
        await using var conn = new SqlConnection(_connStr);
        return await conn.ExecuteAsync(sql, new { Id = id, Status = newStatus, Actor = actorBadge });
    }

}