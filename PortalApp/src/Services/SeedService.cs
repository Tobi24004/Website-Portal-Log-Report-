using Dapper;
using Portal.Api.Infrastructure;

namespace Portal.Api.Services;

/// <summary>
/// Chạy một lần khi khởi động: thay các giá trị PLACEHOLDER trong seed SQL bằng
/// giá trị thật — băm mật khẩu tài khoản demo và MÃ HÓA mật khẩu kết nối nguồn.
/// Idempotent: chỉ tác động khi còn placeholder / chưa mã hóa.
/// </summary>
public sealed class SeedService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ISecretProtector _protector;
    private readonly IConfiguration _config;
    private readonly ILogger<SeedService> _logger;

    public SeedService(IDbConnectionFactory factory, ISecretProtector protector,
        IConfiguration config, ILogger<SeedService> logger)
    {
        _factory = factory;
        _protector = protector;
        _config = config;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        if (!string.Equals(_config["Portal:Seed:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
            return;

        var adminPwd = _config["Portal:Seed:AdminPassword"] ?? "Admin@123";
        var defaultPwd = _config["Portal:Seed:DefaultPassword"] ?? "Portal@123";
        var sourcePwd = _config["Portal:Seed:SourcePassword"];

        using var conn = _factory.CreateMeta();
        conn.Open();

        // 1) Băm mật khẩu các tài khoản còn placeholder.
        var users = (await conn.QueryAsync<(int UserId, string Username, string PasswordHash)>(
            "SELECT UserId, Username, PasswordHash FROM portal.Users WHERE IsDeleted = 0;")).ToList();

        int fixedUsers = 0;
        foreach (var u in users)
        {
            if (!PasswordHasher.IsPlaceholder(u.PasswordHash)) continue;
            var pwd = u.Username.Equals("admin", StringComparison.OrdinalIgnoreCase) ? adminPwd : defaultPwd;
            var hash = PasswordHasher.Hash(pwd);
            await conn.ExecuteAsync(
                "UPDATE portal.Users SET PasswordHash = @Hash WHERE UserId = @UserId;",
                new { Hash = hash, u.UserId });
            fixedUsers++;
        }
        if (fixedUsers > 0)
            _logger.LogInformation("Seed: đã băm mật khẩu cho {Count} tài khoản demo.", fixedUsers);

        // 2) Mã hóa mật khẩu kết nối nguồn còn placeholder / chưa mã hóa.
        if (!string.IsNullOrWhiteSpace(sourcePwd))
        {
            var conns = (await conn.QueryAsync<(int ConnectionId, string ConnectionName, string PasswordEncrypted)>(
                "SELECT ConnectionId, ConnectionName, PasswordEncrypted FROM portal.DatabaseConnections WHERE IsDeleted = 0;")).ToList();

            foreach (var c in conns)
            {
                var needsFix = _protector is null
                    || c.PasswordEncrypted.StartsWith("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
                    || !_protector.IsEncrypted(c.PasswordEncrypted);
                if (!needsFix) continue;

                // Chỉ tự điền cho connection nguồn mẫu (portal_readonly). Các connection
                // khác do admin tự nhập mật khẩu qua giao diện.
                var enc = _protector!.Encrypt(sourcePwd!);
                await conn.ExecuteAsync(
                    "UPDATE portal.DatabaseConnections SET PasswordEncrypted = @Enc, UpdatedAt = SYSUTCDATETIME() WHERE ConnectionId = @Id;",
                    new { Enc = enc, Id = c.ConnectionId });
                _logger.LogInformation("Seed: đã mã hóa mật khẩu cho kết nối '{Name}'.", c.ConnectionName);
            }
        }
    }
}
