using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace police_report_request_backend.Data;

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
