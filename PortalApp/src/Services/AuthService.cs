using Dapper;
using Portal.Api.Infrastructure;
using Portal.Api.Models;

namespace Portal.Api.Services;

public sealed class AuthService
{
    private readonly IDbConnectionFactory _factory;
    private readonly TokenService _tokens;
    private readonly AuditService _audit;

    public AuthService(IDbConnectionFactory factory, TokenService tokens, AuditService audit)
    {
        _factory = factory;
        _tokens = tokens;
        _audit = audit;
    }

    public async Task<LoginResponse?> LoginAsync(string username, string password, string? clientIp)
    {
        using var conn = _factory.CreateMeta();

        var row = await conn.QuerySingleOrDefaultAsync(
            """
            SELECT UserId, Username, FullName, Email, Department, PasswordHash, IsActive
            FROM portal.Users
            WHERE Username = @Username AND IsDeleted = 0;
            """, new { Username = username });

        if (row is null)
            return null;

        if (!(bool)row.IsActive)
            return null;

        string storedHash = row.PasswordHash;
        if (!PasswordHasher.Verify(password, storedHash))
        {
            await _audit.LogAsync((int)row.UserId, "AUTH.LOGIN.FAILED", "User", ((int)row.UserId).ToString(), clientIp: clientIp);
            return null;
        }

        int userId = row.UserId;
        await conn.ExecuteAsync(
            "UPDATE portal.Users SET LastLoginAt = SYSUTCDATETIME() WHERE UserId = @UserId;",
            new { UserId = userId });

        var info = await LoadUserInfoAsync(conn, userId);
        if (info is null) return null;

        var (token, expires) = _tokens.Create(info);
        await _audit.LogAsync(userId, "AUTH.LOGIN", "User", userId.ToString(), clientIp: clientIp);
        return new LoginResponse(token, expires, info);
    }

    public async Task<UserInfo?> GetMeAsync(int userId)
    {
        using var conn = _factory.CreateMeta();
        return await LoadUserInfoAsync(conn, userId);
    }

    /// <summary>
    /// Người dùng tự đổi mật khẩu (Bước 4). Xóa cờ MustChangePassword và cấp
    /// token mới (claim không còn "mustchange"). Trả null nếu mật khẩu hiện tại sai.
    /// </summary>
    public async Task<LoginResponse?> ChangePasswordAsync(int userId, string currentPassword, string newPassword)
    {
        using var conn = _factory.CreateMeta();
        var hash = await conn.ExecuteScalarAsync<string?>(
            "SELECT PasswordHash FROM portal.Users WHERE UserId = @UserId AND IsDeleted = 0 AND IsActive = 1;",
            new { UserId = userId });
        if (hash is null || !PasswordHasher.Verify(currentPassword, hash))
            return null;

        var newHash = PasswordHasher.Hash(newPassword);
        await conn.ExecuteAsync(
            """
            UPDATE portal.Users
            SET PasswordHash = @Hash, MustChangePassword = 0, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @UserId
            WHERE UserId = @UserId;
            """, new { Hash = newHash, UserId = userId });

        var info = await LoadUserInfoAsync(conn, userId);
        if (info is null) return null;
        var (token, expires) = _tokens.Create(info);
        return new LoginResponse(token, expires, info);
    }

    private static async Task<UserInfo?> LoadUserInfoAsync(System.Data.IDbConnection conn, int userId)
    {
        var u = await conn.QuerySingleOrDefaultAsync(
            """
            SELECT UserId, Username, FullName, Email, Department, MustChangePassword
            FROM portal.Users WHERE UserId = @UserId AND IsDeleted = 0 AND IsActive = 1;
            """, new { UserId = userId });
        if (u is null) return null;

        var roles = (await conn.QueryAsync<string>(
            """
            SELECT r.RoleName FROM portal.UserRoles ur
            JOIN portal.Roles r ON r.RoleId = ur.RoleId AND r.IsDeleted = 0
            WHERE ur.UserId = @UserId;
            """, new { UserId = userId })).ToList();

        var perms = (await conn.QueryAsync<string>(
            """
            SELECT DISTINCT p.PermissionCode
            FROM portal.UserRoles ur
            JOIN portal.RolePermissions rp ON rp.RoleId = ur.RoleId
            JOIN portal.Permissions p ON p.PermissionId = rp.PermissionId
            WHERE ur.UserId = @UserId;
            """, new { UserId = userId })).ToList();

        return new UserInfo((int)u.UserId, (string)u.Username, (string?)u.FullName,
            (string?)u.Email, (string?)u.Department, roles, perms, (bool)u.MustChangePassword);
    }
}
