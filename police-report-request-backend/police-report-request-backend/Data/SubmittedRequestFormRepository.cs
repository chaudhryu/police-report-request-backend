using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace police_report_request_backend.Data;
// Add at the top of the file
public sealed class SubmittedRequestListItem
{
    public int Id { get; set; }
    public string Submitter { get; set; } = default!;  // display name or badge
    public DateTime CreatedDate { get; set; }          // UTC from DB
    public string Status { get; set; } = default!;
}

// Add inside SubmittedRequestFormRepository class
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

    // Optional: fetch by id (handy for confirming inserts / building a details page)
    public async Task<string?> GetJsonByIdAsync(int id)
    {
        const string sql = "SELECT SubmittedRequestData FROM dbo.submitted_request_form WHERE Id = @Id;";
        await using var conn = Conn();
        return await conn.ExecuteScalarAsync<string?>(sql, new { Id = id });
    }
}
