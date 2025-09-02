using Dapper;
using Microsoft.Data.SqlClient;
using police_report_request_backend.Models;

namespace police_report_request_backend.Data;

public sealed class UsersRepository
{
    private readonly string _connStr;
    public UsersRepository(IConfiguration cfg)
        => _connStr = cfg.GetConnectionString("DefaultConnection")!;

    // Upsert by Badge: try UPDATE; if no rows, INSERT.
    public async Task UpsertAsync(UserRow u, string actorEmail)
    {
        await using var conn = new SqlConnection(_connStr);
        var now = DateTime.UtcNow;

        var updated = await conn.ExecuteAsync(@"
UPDATE dbo.Users
SET FirstName=@FirstName,
    LastName=@LastName,
    DisplayName=@DisplayName,
    Email=@Email,
    [Position]=@Position,
    IsAdmin=@IsAdmin,
    LastUpdatedBy=@Actor,
    LastUpdatedDate=@Now
WHERE Badge=@Badge;",
            new
            {
                u.Badge,
                u.FirstName,
                u.LastName,
                u.DisplayName,
                u.Email,
                u.Position,
                u.IsAdmin,
                Actor = actorEmail,
                Now = now
            });

        if (updated == 0)
        {
            await conn.ExecuteAsync(@"
INSERT INTO dbo.Users
(Badge, FirstName, LastName, DisplayName, Email, [Position], IsAdmin, CreatedDate, LastUpdatedBy, LastUpdatedDate)
VALUES
(@Badge, @FirstName, @LastName, @DisplayName, @Email, @Position, @IsAdmin, @Now, @Actor, @Now);",
                new
                {
                    u.Badge,
                    u.FirstName,
                    u.LastName,
                    u.DisplayName,
                    u.Email,
                    u.Position,
                    u.IsAdmin,
                    Actor = actorEmail,
                    Now = now
                });
        }
    }

    public async Task<UserRow?> GetByBadgeAsync(string badge)
    {
        await using var conn = new SqlConnection(_connStr);
        return await conn.QuerySingleOrDefaultAsync<UserRow>(
            "SELECT * FROM dbo.Users WHERE Badge=@Badge;", new { Badge = badge });
    }

    public async Task<int> DeleteAsync(string badge)
    {
        await using var conn = new SqlConnection(_connStr);
        return await conn.ExecuteAsync(
            "DELETE FROM dbo.Users WHERE Badge=@Badge;", new { Badge = badge });
    }
}
