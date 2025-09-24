using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using police_report_request_backend.Models;

namespace police_report_request_backend.Data;

public sealed class UsersRepository
{
    private readonly string _connStr;

    public UsersRepository(IConfiguration cfg)
    {
        _connStr = cfg.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is missing.");
    }

    private SqlConnection Conn() => new SqlConnection(_connStr);

    /// <summary>
    /// Upsert by Badge: UPDATE first; if no rows affected, INSERT.
    /// IMPORTANT: If <paramref name="u"/>.IsAdmin is null, the UPDATE will preserve the existing DB value.
    /// On INSERT, IsAdmin defaults to 0 when null.
    /// </summary>
    public async Task UpsertAsync(UserRow u, string actorEmail)
    {
        var now = DateTime.UtcNow;

        const string updateSql = @"
UPDATE dbo.Users
SET FirstName       = @FirstName,
    LastName        = @LastName,
    DisplayName     = @DisplayName,
    Email           = @Email,
    [Position]      = @Position,
    IsAdmin         = COALESCE(@IsAdmin, IsAdmin),   -- preserve existing if NULL is passed
    LastUpdatedBy   = @Actor,
    LastUpdatedDate = @Now
WHERE Badge = @Badge;";

        const string insertSql = @"
INSERT INTO dbo.Users
( Badge,  FirstName,  LastName,  DisplayName,  Email,  [Position],  IsAdmin,                 CreatedDate, LastUpdatedBy, LastUpdatedDate )
VALUES
( @Badge, @FirstName, @LastName, @DisplayName, @Email, @Position,   COALESCE(@IsAdmin, 0),   @Now,        @Actor,        @Now );";

        await using var conn = Conn();
        await conn.OpenAsync();

        await using var tx = await conn.BeginTransactionAsync();

        var updated = await conn.ExecuteAsync(updateSql, new
        {
            u.Badge,
            u.FirstName,
            u.LastName,
            u.DisplayName,
            u.Email,
            u.Position,
            u.IsAdmin, // NULL => preserve current IsAdmin via COALESCE
            Actor = actorEmail,
            Now = now
        }, tx);

        if (updated == 0)
        {
            await conn.ExecuteAsync(insertSql, new
            {
                u.Badge,
                u.FirstName,
                u.LastName,
                u.DisplayName,
                u.Email,
                u.Position,
                u.IsAdmin, // NULL => default to 0 on insert
                Actor = actorEmail,
                Now = now
            }, tx);
        }

        await tx.CommitAsync();
    }

    public async Task<UserRow?> GetByBadgeAsync(string badge)
    {
        const string sql = @"
SELECT TOP 1
    Badge,
    FirstName,
    LastName,
    DisplayName,
    Email,
    [Position],
    IsAdmin,
    CreatedDate,
    LastUpdatedBy,
    LastUpdatedDate
FROM dbo.Users
WHERE Badge = @Badge;";

        await using var conn = Conn();
        return await conn.QuerySingleOrDefaultAsync<UserRow>(sql, new { Badge = badge });
    }

    public async Task<int> DeleteAsync(string badge)
    {
        const string sql = "DELETE FROM dbo.Users WHERE Badge = @Badge;";
        await using var conn = Conn();
        return await conn.ExecuteAsync(sql, new { Badge = badge });
    }

    /// <summary>
    /// Explicit admin toggle helper (used by the optional /api/users/{badge}/admin endpoint).
    /// </summary>
    public async Task<int> SetAdminAsync(string badge, bool isAdmin, string actorEmail)
    {
        const string sql = @"
UPDATE dbo.Users
SET IsAdmin = @IsAdmin,
    LastUpdatedBy = @Actor,
    LastUpdatedDate = SYSUTCDATETIME()
WHERE Badge = @Badge;";

        await using var conn = Conn();
        return await conn.ExecuteAsync(sql, new
        {
            Badge = badge,
            IsAdmin = isAdmin ? 1 : 0,
            Actor = actorEmail
        });
    }
    // UsersRepository.cs  (add inside the UsersRepository class)
    public async Task<UserRow?> GetByEmailAsync(string email)
    {
        const string sql = @"
SELECT TOP 1
    Badge,
    FirstName,
    LastName,
    DisplayName,
    Email,
    [Position],
    IsAdmin,
    CreatedDate,
    LastUpdatedBy,
    LastUpdatedDate
FROM dbo.Users
WHERE Email = @Email;";

        await using var conn = Conn();
        return await conn.QuerySingleOrDefaultAsync<UserRow>(sql, new { Email = email });
    }

}
