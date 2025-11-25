// Data/UsersRepository.cs  -- Update-only Upsert; explicit admin create when requested.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using police_report_request_backend.Models;

namespace police_report_request_backend.Data
{
    public sealed class UsersRepository
    {
        private readonly string _connStr;

        public UsersRepository(IConfiguration cfg)
        {
            _connStr = cfg.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is missing.");
        }

        private SqlConnection Conn() => new SqlConnection(_connStr);

        // ----------------------------------------------------------------
        // READS
        // ----------------------------------------------------------------

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

        public async Task<IReadOnlyList<UserRow>> ListAsync(string? q, int skip = 0, int take = 200)
        {
            const string sql = @"
SELECT
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
WHERE
    (@HasQ = 0)
    OR (Badge LIKE @QLike
        OR UPPER(LTRIM(RTRIM(DisplayName))) LIKE @QUpperLike
        OR UPPER(LTRIM(RTRIM(FirstName)))   LIKE @QUpperLike
        OR UPPER(LTRIM(RTRIM(LastName)))    LIKE @QUpperLike
        OR UPPER(LTRIM(RTRIM(Email)))       LIKE @QUpperLike
        OR UPPER(LTRIM(RTRIM([Position])))  LIKE @QUpperLike)
ORDER BY
    COALESCE(NULLIF(LTRIM(RTRIM(DisplayName)), ''), Badge) ASC,
    Badge ASC
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;";

            var hasQ = !string.IsNullOrWhiteSpace(q);

            await using var conn = Conn();
            var rows = await conn.QueryAsync<UserRow>(sql, new
            {
                HasQ = hasQ ? 1 : 0,
                QLike = hasQ ? $"%{q!.Trim()}%" : null,
                QUpperLike = hasQ ? $"%{q!.Trim().ToUpperInvariant()}%" : null,
                Skip = skip < 0 ? 0 : skip,
                Take = take <= 0 ? 200 : take
            });
            return rows.AsList();
        }

        // ----------------------------------------------------------------
        // WRITES
        // ----------------------------------------------------------------

        /// <summary>
        /// Update-only "upsert": updates the existing row for this badge.
        /// If the badge does not exist, NO INSERT is performed (0 rows affected).
        /// Callers that need to create a user must use an explicit method (e.g., EnsureAdminAsync).
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
    IsAdmin         = COALESCE(@IsAdmin, IsAdmin),
    LastUpdatedBy   = @Actor,
    LastUpdatedDate = @Now
WHERE Badge = @Badge;";

            await using var conn = Conn();
            await conn.ExecuteAsync(updateSql, new
            {
                u.Badge,
                u.FirstName,
                u.LastName,
                u.DisplayName,
                u.Email,
                u.Position,
                u.IsAdmin, // NULL -> preserve existing via COALESCE
                Actor = string.IsNullOrWhiteSpace(actorEmail) ? "system" : actorEmail.Trim(),
                Now = now
            });

            // Intentionally DO NOT insert if 0 rows were updated.
        }

        /// <summary>
        /// Explicit admin toggle (updates existing row only).
        /// Returns affected rows (0 when badge does not exist).
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
                Actor = string.IsNullOrWhiteSpace(actorEmail) ? "system" : actorEmail.Trim()
            });
        }

        /// <summary>
        /// Delete a user by badge. (No cascade; callers must ensure this is safe.)
        /// </summary>
        public async Task<int> DeleteAsync(string badge)
        {
            const string sql = "DELETE FROM dbo.Users WHERE Badge = @Badge;";
            await using var conn = Conn();
            return await conn.ExecuteAsync(sql, new { Badge = badge });
        }

        /// <summary>
        /// Explicitly ensure that the given badge exists AND is admin.
        /// - If the row exists: sets IsAdmin = 1 and backfills Email/DisplayName/First/Last when blank.
        /// - If the row does not exist: inserts a minimal admin record with derived First/Last from DisplayName.
        /// </summary>
        public async Task EnsureAdminAsync(string badge, string? email, string? displayName, string actorEmail)
        {
            if (string.IsNullOrWhiteSpace(badge))
                throw new ArgumentException("badge required", nameof(badge));

            // QUICK FIX: derive first/last from the provided DisplayName (no controller/front-end changes needed)
            var (derivedFirst, derivedLast) = NamePartsFromDisplay(displayName);

            const string sql = @"
SET NOCOUNT ON;
DECLARE @Now datetime = GETUTCDATE();

IF EXISTS (SELECT 1 FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE Badge = @Badge)
BEGIN
    UPDATE dbo.Users
    SET IsAdmin         = 1,
        -- Backfill only when blank
        FirstName       = CASE
                              WHEN (FirstName IS NULL OR LTRIM(RTRIM(FirstName)) = '')
                                   AND @DerivedFirst IS NOT NULL AND @DerivedFirst <> '' THEN @DerivedFirst
                              ELSE FirstName
                          END,
        LastName        = CASE
                              WHEN (LastName IS NULL OR LTRIM(RTRIM(LastName)) = '')
                                   AND @DerivedLast IS NOT NULL AND @DerivedLast <> '' THEN @DerivedLast
                              ELSE LastName
                          END,
        DisplayName     = CASE
                              WHEN (DisplayName IS NULL OR LTRIM(RTRIM(DisplayName)) = '')
                              THEN COALESCE(NULLIF(@DisplayName, ''),
                                            NULLIF(LTRIM(RTRIM(
                                                CONCAT(COALESCE(@DerivedFirst, ''),
                                                       CASE WHEN @DerivedFirst IS NOT NULL AND @DerivedLast IS NOT NULL THEN ' ' ELSE '' END,
                                                       COALESCE(@DerivedLast, ''))
                                            )), ''))
                              ELSE DisplayName
                          END,
        Email           = CASE
                              WHEN (Email IS NULL OR LTRIM(RTRIM(Email)) = '')
                                   AND @Email IS NOT NULL AND @Email <> '' THEN @Email
                              ELSE Email
                          END,
        LastUpdatedBy   = @Actor,
        LastUpdatedDate = @Now
    WHERE Badge = @Badge;
END
ELSE
BEGIN
    INSERT INTO dbo.Users
        (Badge, FirstName, LastName, DisplayName, Email, [Position], IsAdmin, CreatedDate, LastUpdatedBy, LastUpdatedDate)
    VALUES
        (@Badge,
         NULLIF(@DerivedFirst, ''),
         NULLIF(@DerivedLast,  ''),
         COALESCE(NULLIF(@DisplayName, ''),
                  NULLIF(LTRIM(RTRIM(CONCAT(COALESCE(@DerivedFirst, ''),
                                            CASE WHEN @DerivedFirst IS NOT NULL AND @DerivedLast IS NOT NULL THEN ' ' ELSE '' END,
                                            COALESCE(@DerivedLast,  '')))), '')),
         NULLIF(@Email, ''),
         NULL, 1, @Now, @Actor, @Now);
END";
            await using var conn = Conn();
            await conn.ExecuteAsync(sql, new
            {
                Badge = badge.Trim(),
                Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
                DerivedFirst = string.IsNullOrWhiteSpace(derivedFirst) ? null : derivedFirst,
                DerivedLast = string.IsNullOrWhiteSpace(derivedLast) ? null : derivedLast,
                Actor = string.IsNullOrWhiteSpace(actorEmail) ? "system" : actorEmail.Trim()
            });
        }

        // ----------------- helpers -----------------
        private static (string? First, string? Last) NamePartsFromDisplay(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return (null, null);
            var trimmed = displayName.Trim();

            // Split on first space: "First Last Last2..." -> First = first token, Last = rest
            var idx = trimmed.IndexOf(' ');
            if (idx <= 0) return (trimmed, null);

            var first = trimmed.Substring(0, idx).Trim();
            var last = trimmed.Substring(idx + 1).Trim();

            return (string.IsNullOrWhiteSpace(first) ? null : first,
                    string.IsNullOrWhiteSpace(last) ? null : last);
        }
    }
}
