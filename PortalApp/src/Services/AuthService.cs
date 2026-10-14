using Dapper;
using Portal.Api.Infrastructure;
using Portal.Api.Models;

namespace Portal.Api.Services;

/// <summary>Kết quả đăng nhập: thành công / sai thông tin / khóa tạm brute-force / bị quản trị khóa.</summary>
public enum LoginStatus { Success, InvalidCredentials, Locked, Disabled }
public sealed record LoginResult(LoginStatus Status, LoginResponse? Response = null, int LockoutRemainingSeconds = 0);

public sealed class AuthService
{
    private readonly IDbConnectionFactory _factory;
    private readonly TokenService _tokens;
    private readonly AuditService _audit;
    private readonly int _maxFailed;
    private readonly int _lockoutMinutes;

    public AuthService(IDbConnectionFactory factory, TokenService tokens, AuditService audit, IConfiguration config)
    {
        _factory = factory;
        _tokens = tokens;
        _audit = audit;
        _maxFailed = Math.Max(3, config.GetValue("Portal:Security:MaxFailedAttempts", 5));
        _lockoutMinutes = Math.Max(1, config.GetValue("Portal:Security:LockoutMinutes", 15));
    }

    public async Task<LoginResult> LoginAsync(string username, string password, string? clientIp)
    {
        using var conn = _factory.CreateMeta();

        var row = await conn.QuerySingleOrDefaultAsync(
            """
            SELECT UserId, Username, PasswordHash, IsActive, FailedLoginCount, LockoutUntil
            FROM portal.Users
            WHERE Username = @Username AND IsDeleted = 0;
            """, new { Username = username });

        // Tài khoản không tồn tại -> trả "sai thông tin" (tránh dò username).
        if (row is null)
            return new LoginResult(LoginStatus.InvalidCredentials);

        int userId = row.UserId;
        bool isActive = row.IsActive;
        DateTime? lockoutUntil = row.LockoutUntil;
        int failed = row.FailedLoginCount;
        var now = DateTime.UtcNow;

        // Đang trong thời gian khóa tạm -> từ chối ngay (kể cả mật khẩu đúng).
        if (lockoutUntil.HasValue && lockoutUntil.Value > now)
        {
            await _audit.LogAsync(userId, "AUTH.LOGIN.LOCKED", "User", userId.ToString(), clientIp: clientIp);
            return new LoginResult(LoginStatus.Locked, LockoutRemainingSeconds: (int)(lockoutUntil.Value - now).TotalSeconds);
        }

        string storedHash = row.PasswordHash;
        if (!PasswordHasher.Verify(password, storedHash))
        {
            int newCount = failed + 1;
            if (newCount >= _maxFailed)
            {
                // Vượt ngưỡng -> khóa tạm và reset bộ đếm.
                var until = now.AddMinutes(_lockoutMinutes);
                await conn.ExecuteAsync(
                    "UPDATE portal.Users SET FailedLoginCount = 0, LockoutUntil = @Until WHERE UserId = @UserId;",
                    new { Until = until, UserId = userId });
                await _audit.LogAsync(userId, "AUTH.LOCKOUT", "User", userId.ToString(),
                    newValue: $"locked {_lockoutMinutes}m", clientIp: clientIp);
                return new LoginResult(LoginStatus.Locked, LockoutRemainingSeconds: _lockoutMinutes * 60);
            }
            await conn.ExecuteAsync(
                "UPDATE portal.Users SET FailedLoginCount = @C WHERE UserId = @UserId;",
                new { C = newCount, UserId = userId });
            await _audit.LogAsync(userId, "AUTH.LOGIN.FAILED", "User", userId.ToString(),
                newValue: $"attempt {newCount}/{_maxFailed}", clientIp: clientIp);
            return new LoginResult(LoginStatus.InvalidCredentials);
        }

        // Mật khẩu đúng nhưng tài khoản bị quản trị khóa -> báo rõ ràng, không cấp token.
        if (!isActive)
        {
            await conn.ExecuteAsync(
                "UPDATE portal.Users SET FailedLoginCount = 0 WHERE UserId = @UserId;", new { UserId = userId });
            await _audit.LogAsync(userId, "AUTH.LOGIN.DISABLED", "User", userId.ToString(), clientIp: clientIp);
            return new LoginResult(LoginStatus.Disabled);
        }

        // Đúng mật khẩu & tài khoản còn hiệu lực -> xóa bộ đếm & khóa, cập nhật lần đăng nhập cuối.
        await conn.ExecuteAsync(
            "UPDATE portal.Users SET FailedLoginCount = 0, LockoutUntil = NULL, LastLoginAt = SYSUTCDATETIME() WHERE UserId = @UserId;",
            new { UserId = userId });

        var info = await LoadUserInfoAsync(conn, userId);
        if (info is null) return new LoginResult(LoginStatus.InvalidCredentials);

        var (token, expires) = _tokens.Create(info);
        await _audit.LogAsync(userId, "AUTH.LOGIN", "User", userId.ToString(), clientIp: clientIp);
        return new LoginResult(LoginStatus.Success, new LoginResponse(token, expires, info));
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
