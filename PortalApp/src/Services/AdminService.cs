using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Portal.Api.Infrastructure;
using Portal.Api.Models;

namespace Portal.Api.Services;

public sealed class AdminService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ISecretProtector _protector;

    public AdminService(IDbConnectionFactory factory, ISecretProtector protector)
    {
        _factory = factory;
        _protector = protector;
    }

    // ---- Users ----
    public async Task<IReadOnlyList<AdminUser>> GetUsersAsync()
    {
        using var conn = _factory.CreateMeta();
        var rows = await conn.QueryAsync(
            """
            SELECT u.UserId, u.Username, u.FullName, u.Email, u.Department, u.IsActive, u.LastLoginAt,
                   (SELECT STRING_AGG(r.RoleName, N',') FROM portal.UserRoles ur
                    JOIN portal.Roles r ON r.RoleId = ur.RoleId WHERE ur.UserId = u.UserId) AS Roles
            FROM portal.Users u
            WHERE u.IsDeleted = 0
            ORDER BY u.Username;
            """);
        return rows.Select(r => new AdminUser((int)r.UserId, (string)r.Username, (string?)r.FullName,
            (string?)r.Email, (string?)r.Department, (bool)r.IsActive, (DateTime?)r.LastLoginAt,
            ((string?)r.Roles)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>())).ToList();
    }

    public async Task<int> CreateUserAsync(CreateUserRequest req, int actingUserId)
    {
        using var conn = _factory.CreateMeta();
        conn.Open();
        using var tx = conn.BeginTransaction();

        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM portal.Users WHERE Username = @Username AND IsDeleted = 0;",
            new { req.Username }, tx);
        if (exists > 0) throw new InvalidOperationException("Tên đăng nhập đã tồn tại.");

        var hash = PasswordHasher.Hash(req.Password);
        // Tài khoản do Admin tạo dùng mật khẩu tạm -> bắt buộc đổi ở lần đăng nhập đầu.
        var userId = await conn.ExecuteScalarAsync<int>(
            """
            INSERT INTO portal.Users (Username, FullName, Email, Department, PasswordHash, IsActive, MustChangePassword, CreatedBy)
            OUTPUT INSERTED.UserId
            VALUES (@Username, @FullName, @Email, @Department, @Hash, 1, 1, @By);
            """, new { req.Username, req.FullName, req.Email, req.Department, Hash = hash, By = actingUserId }, tx);

        if (req.Roles is { Length: > 0 })
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO portal.UserRoles (UserId, RoleId, AssignedBy)
                SELECT @UserId, r.RoleId, @By FROM portal.Roles r
                WHERE r.RoleName IN @Roles AND r.IsDeleted = 0;
                """, new { UserId = userId, By = actingUserId, Roles = req.Roles }, tx);
        }
        tx.Commit();
        return userId;
    }

    public async Task ResetPasswordAsync(int userId, string newPassword, int actingUserId)
    {
        using var conn = _factory.CreateMeta();
        var hash = PasswordHasher.Hash(newPassword);
        // Admin đặt lại mật khẩu = mật khẩu tạm -> bắt buộc người dùng đổi lại.
        await conn.ExecuteAsync(
            "UPDATE portal.Users SET PasswordHash = @Hash, MustChangePassword = 1, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @By WHERE UserId = @UserId AND IsDeleted = 0;",
            new { Hash = hash, By = actingUserId, UserId = userId });
    }

    public async Task SetActiveAsync(int userId, bool isActive, int actingUserId)
    {
        using var conn = _factory.CreateMeta();
        await conn.ExecuteAsync(
            "UPDATE portal.Users SET IsActive = @IsActive, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @By WHERE UserId = @UserId AND IsDeleted = 0;",
            new { IsActive = isActive, By = actingUserId, UserId = userId });
    }

    // ---- Connections ----
    public async Task<IReadOnlyList<AdminConnection>> GetConnectionsAsync()
    {
        using var conn = _factory.CreateMeta();
        var rows = await conn.QueryAsync(
            """
            SELECT ConnectionId, ConnectionName, ServerHost, Port, DatabaseName, Username,
                   PasswordEncrypted, Environment, IsActive
            FROM portal.DatabaseConnections WHERE IsDeleted = 0 ORDER BY ConnectionName;
            """);
        // KHÔNG trả mật khẩu; chỉ cho biết đã được mã hóa hay chưa.
        return rows.Select(r => new AdminConnection((int)r.ConnectionId, (string)r.ConnectionName,
            (string)r.ServerHost, (int?)r.Port, (string)r.DatabaseName, (string)r.Username,
            (string)r.Environment, (bool)r.IsActive, _protector.IsEncrypted((string?)r.PasswordEncrypted))).ToList();
    }

    public async Task<TestConnectionResult> TestConnectionAsync(int connectionId)
    {
        using var meta = _factory.CreateMeta();
        var row = await meta.QuerySingleOrDefaultAsync(
            """
            SELECT ConnectionId, ServerHost, Port, DatabaseName, Username, PasswordEncrypted,
                   ConnectionTimeout, CommandTimeout
            FROM portal.DatabaseConnections WHERE ConnectionId = @Id AND IsDeleted = 0;
            """, new { Id = connectionId });
        if (row is null) return new TestConnectionResult(false, "Không tìm thấy kết nối.", 0);

        var info = new SourceConnectionInfo((int)row.ConnectionId, (string)row.ServerHost, (int?)row.Port,
            (string)row.DatabaseName, (string)row.Username, (int)row.ConnectionTimeout, (int)row.CommandTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            var pwd = _protector.Decrypt((string)row.PasswordEncrypted);
            await using var src = _factory.CreateSource(info, pwd);
            await src.OpenAsync();
            await using var cmd = new SqlCommand("SELECT 1;", src) { CommandTimeout = 10 };
            await cmd.ExecuteScalarAsync();
            sw.Stop();
            return new TestConnectionResult(true, "Kết nối thành công.", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestConnectionResult(false, "Kết nối thất bại: " + ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    // ---- Templates ----
    public async Task<IReadOnlyList<AdminTemplate>> GetTemplatesAsync()
    {
        using var conn = _factory.CreateMeta();
        var rows = await conn.QueryAsync(
            """
            SELECT t.TemplateId, t.TemplateCode, t.TemplateName, c.CategoryName, dc.ConnectionName, t.Status,
                   (SELECT COUNT(*) FROM portal.ReportTemplateParameters p WHERE p.TemplateId = t.TemplateId) AS ParameterCount
            FROM portal.ReportTemplates t
            JOIN portal.ReportCategories c ON c.CategoryId = t.CategoryId
            JOIN portal.DatabaseConnections dc ON dc.ConnectionId = t.ConnectionId
            WHERE t.IsDeleted = 0 ORDER BY c.DisplayOrder, t.TemplateName;
            """);
        return rows.Select(r => new AdminTemplate((int)r.TemplateId, (string)r.TemplateCode,
            (string)r.TemplateName, (string)r.CategoryName, (string)r.ConnectionName,
            (string)r.Status, (int)r.ParameterCount)).ToList();
    }

    // ---- Lookups (danh mục / kết nối / vai trò) cho form tạo-sửa template ----
    public async Task<AdminLookups> GetLookupsAsync()
    {
        using var conn = _factory.CreateMeta();
        var cats = (await conn.QueryAsync<Lookup>(
            "SELECT CategoryId AS Id, CategoryName AS Name FROM portal.ReportCategories WHERE IsDeleted = 0 ORDER BY DisplayOrder, CategoryName;")).ToList();
        var conns = (await conn.QueryAsync<Lookup>(
            "SELECT ConnectionId AS Id, ConnectionName AS Name FROM portal.DatabaseConnections WHERE IsDeleted = 0 AND IsActive = 1 ORDER BY ConnectionName;")).ToList();
        var roles = (await conn.QueryAsync<Lookup>(
            "SELECT RoleId AS Id, RoleName AS Name FROM portal.Roles WHERE IsDeleted = 0 ORDER BY RoleName;")).ToList();
        return new AdminLookups(cats, conns, roles);
    }

    // ---- Lấy đầy đủ 1 template để sửa ----
    public async Task<TemplateFull?> GetTemplateFullAsync(int id)
    {
        using var conn = _factory.CreateMeta();
        var t = await conn.QuerySingleOrDefaultAsync(
            """
            SELECT TemplateId, TemplateCode, TemplateName, Description, CategoryId, ConnectionId,
                   SqlCommand, MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status
            FROM portal.ReportTemplates WHERE TemplateId = @Id AND IsDeleted = 0;
            """, new { Id = id });
        if (t is null) return null;

        var roleIds = (await conn.QueryAsync<int>(
            "SELECT RoleId FROM portal.ReportTemplatePermissions WHERE TemplateId = @Id AND RoleId IS NOT NULL;",
            new { Id = id })).ToList();

        var ps = await conn.QueryAsync(
            """
            SELECT ParameterId, ParameterName, DisplayName, DataType, InputType, IsRequired,
                   DefaultValue, DisplayOrder, MinValue, MaxValue, MaxLength, Options
            FROM portal.ReportTemplateParameters WHERE TemplateId = @Id ORDER BY DisplayOrder, ParameterId;
            """, new { Id = id });
        var parameters = ps.Select(p => new TemplateParameter(
            (int)p.ParameterId, (string)p.ParameterName, (string)p.DisplayName, (string)p.DataType,
            (string)p.InputType, (bool)p.IsRequired, (string?)p.DefaultValue, (int)p.DisplayOrder,
            (string?)p.MinValue, (string?)p.MaxValue, (int?)p.MaxLength, (string?)p.Options)).ToList();

        return new TemplateFull((int)t.TemplateId, (string)t.TemplateCode, (string)t.TemplateName,
            (string?)t.Description, (int)t.CategoryId, (int)t.ConnectionId, (string)t.SqlCommand,
            (int)t.MaxRows, (int)t.TimeoutSeconds, (bool)t.AllowCopy, (bool)t.AllowExport,
            (string)t.Status, roleIds, parameters);
    }

    // ---- Tạo / cập nhật template (có kiểm tra SQL Guard + lưu version) ----
    public async Task<int> SaveTemplateAsync(int? templateId, TemplateFormRequest req, int actingUserId)
    {
        Validate(req);

        using var conn = _factory.CreateMeta();
        conn.Open();
        using var tx = conn.BeginTransaction();

        int id;
        if (templateId is null)
        {
            var dup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM portal.ReportTemplates WHERE TemplateCode = @TemplateCode AND IsDeleted = 0;",
                new { req.TemplateCode }, tx);
            if (dup > 0) throw new InvalidOperationException("Mã báo cáo đã tồn tại.");

            id = await conn.ExecuteScalarAsync<int>(
                """
                INSERT INTO portal.ReportTemplates
                    (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
                     MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
                OUTPUT INSERTED.TemplateId
                VALUES (@TemplateCode, @TemplateName, @Description, @CategoryId, @ConnectionId, @SqlCommand,
                        @MaxRows, @TimeoutSeconds, @AllowCopy, @AllowExport, @Status, @By);
                """,
                new { req.TemplateCode, req.TemplateName, req.Description, req.CategoryId, req.ConnectionId,
                      req.SqlCommand, req.MaxRows, req.TimeoutSeconds, req.AllowCopy, req.AllowExport,
                      req.Status, By = actingUserId }, tx);

            await AddVersionAsync(conn, tx, id, req.SqlCommand, req.VersionNote ?? "Tạo mới", actingUserId);
        }
        else
        {
            id = templateId.Value;
            var oldSql = await conn.ExecuteScalarAsync<string?>(
                "SELECT SqlCommand FROM portal.ReportTemplates WHERE TemplateId = @Id AND IsDeleted = 0;",
                new { Id = id }, tx);
            if (oldSql is null) throw new InvalidOperationException("Không tìm thấy báo cáo.");

            var dup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM portal.ReportTemplates WHERE TemplateCode = @TemplateCode AND IsDeleted = 0 AND TemplateId <> @Id;",
                new { req.TemplateCode, Id = id }, tx);
            if (dup > 0) throw new InvalidOperationException("Mã báo cáo đã tồn tại.");

            await conn.ExecuteAsync(
                """
                UPDATE portal.ReportTemplates
                SET TemplateCode = @TemplateCode, TemplateName = @TemplateName, Description = @Description,
                    CategoryId = @CategoryId, ConnectionId = @ConnectionId, SqlCommand = @SqlCommand,
                    MaxRows = @MaxRows, TimeoutSeconds = @TimeoutSeconds, AllowCopy = @AllowCopy,
                    AllowExport = @AllowExport, Status = @Status, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @By
                WHERE TemplateId = @Id;
                """,
                new { req.TemplateCode, req.TemplateName, req.Description, req.CategoryId, req.ConnectionId,
                      req.SqlCommand, req.MaxRows, req.TimeoutSeconds, req.AllowCopy, req.AllowExport,
                      req.Status, By = actingUserId, Id = id }, tx);

            if (!string.Equals(oldSql, req.SqlCommand, StringComparison.Ordinal))
                await AddVersionAsync(conn, tx, id, req.SqlCommand, req.VersionNote ?? "Cập nhật SQL", actingUserId);
        }

        // Thay toàn bộ tham số.
        await conn.ExecuteAsync("DELETE FROM portal.ReportTemplateParameters WHERE TemplateId = @Id;", new { Id = id }, tx);
        int order = 1;
        foreach (var p in req.Parameters ?? Array.Empty<TemplateParamInput>())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO portal.ReportTemplateParameters
                    (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired,
                     DefaultValue, DisplayOrder, MinValue, MaxValue, MaxLength, Options)
                VALUES (@Id, @ParameterName, @DisplayName, @DataType, @InputType, @IsRequired,
                        @DefaultValue, @DisplayOrder, @MinValue, @MaxValue, @MaxLength, @Options);
                """,
                new { Id = id, ParameterName = NormalizeParamName(p.ParameterName), p.DisplayName,
                      p.DataType, InputType = InputTypeFor(p.DataType), p.IsRequired, p.DefaultValue,
                      DisplayOrder = order++, p.MinValue, p.MaxValue, p.MaxLength, p.Options }, tx);
        }

        // Thay toàn bộ cấp quyền theo VAI TRÒ (giữ nguyên quyền cấp trực tiếp cho user).
        await conn.ExecuteAsync(
            "DELETE FROM portal.ReportTemplatePermissions WHERE TemplateId = @Id AND RoleId IS NOT NULL;",
            new { Id = id }, tx);
        foreach (var roleId in (req.RoleIds ?? Array.Empty<int>()).Distinct())
        {
            await conn.ExecuteAsync(
                "INSERT INTO portal.ReportTemplatePermissions (TemplateId, RoleId, GrantedBy) VALUES (@Id, @RoleId, @By);",
                new { Id = id, RoleId = roleId, By = actingUserId }, tx);
        }

        tx.Commit();
        return id;
    }

    public async Task SoftDeleteTemplateAsync(int id, int actingUserId)
    {
        using var conn = _factory.CreateMeta();
        await conn.ExecuteAsync(
            "UPDATE portal.ReportTemplates SET IsDeleted = 1, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @By WHERE TemplateId = @Id;",
            new { Id = id, By = actingUserId });
    }

    private static async Task AddVersionAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        int templateId, string sql, string note, int by)
    {
        var next = await conn.ExecuteScalarAsync<int>(
            "SELECT ISNULL(MAX(VersionNo), 0) + 1 FROM portal.ReportTemplateVersions WHERE TemplateId = @Id;",
            new { Id = templateId }, tx);
        await conn.ExecuteAsync(
            """
            INSERT INTO portal.ReportTemplateVersions (TemplateId, VersionNo, SqlCommand, Note, ChangedBy)
            VALUES (@Id, @No, @Sql, @Note, @By);
            """, new { Id = templateId, No = next, Sql = sql, Note = note, By = by }, tx);
    }

    private static void Validate(TemplateFormRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TemplateCode)) throw new InvalidOperationException("Mã báo cáo là bắt buộc.");
        if (string.IsNullOrWhiteSpace(req.TemplateName)) throw new InvalidOperationException("Tên báo cáo là bắt buộc.");
        if (req.CategoryId <= 0) throw new InvalidOperationException("Vui lòng chọn nhóm báo cáo.");
        if (req.ConnectionId <= 0) throw new InvalidOperationException("Vui lòng chọn kết nối database.");
        if (req.MaxRows <= 0) throw new InvalidOperationException("Số dòng tối đa phải lớn hơn 0.");
        if (req.TimeoutSeconds <= 0) throw new InvalidOperationException("Timeout phải lớn hơn 0.");
        if (req.Status is not ("Draft" or "Published" or "Disabled"))
            throw new InvalidOperationException("Trạng thái không hợp lệ.");
        var (ok, reason) = SqlGuard.Validate(req.SqlCommand);
        if (!ok) throw new InvalidOperationException("SQL Guard từ chối câu lệnh: " + reason);
        var dataTypes = new[] { "Text", "Integer", "Decimal", "Date", "DateTime", "Dropdown", "Checkbox" };
        foreach (var p in req.Parameters ?? Array.Empty<TemplateParamInput>())
        {
            if (string.IsNullOrWhiteSpace(p.ParameterName)) throw new InvalidOperationException("Tên tham số không được rỗng.");
            if (string.IsNullOrWhiteSpace(p.DisplayName)) throw new InvalidOperationException($"Nhãn hiển thị của tham số '{p.ParameterName}' là bắt buộc.");
            if (!dataTypes.Contains(p.DataType)) throw new InvalidOperationException($"Kiểu dữ liệu '{p.DataType}' không hợp lệ.");
        }
    }

    private static string NormalizeParamName(string name)
    {
        name = name.Trim();
        return name.StartsWith('@') ? name : "@" + name;
    }

    private static string InputTypeFor(string dataType) => dataType switch
    {
        "Integer" or "Decimal" => "Number",
        "Date" => "Date",
        "DateTime" => "DateTime",
        "Dropdown" => "Dropdown",
        "Checkbox" => "Checkbox",
        _ => "Text"
    };

    // ---- Audit ----
    public async Task<IReadOnlyList<AuditItem>> GetAuditAsync(int take = 200)
    {
        using var conn = _factory.CreateMeta();
        var rows = await conn.QueryAsync<AuditItem>(
            """
            SELECT TOP (@Take) a.AuditId, u.Username, a.Action, a.TargetObject, a.TargetId, a.ClientIP, a.CreatedAt
            FROM portal.AuditLogs a
            LEFT JOIN portal.Users u ON u.UserId = a.UserId
            ORDER BY a.CreatedAt DESC;
            """, new { Take = take });
        return rows.ToList();
    }
}
