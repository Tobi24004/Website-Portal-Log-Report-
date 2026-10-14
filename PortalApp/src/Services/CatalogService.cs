using Dapper;
using Portal.Api.Infrastructure;
using Portal.Api.Models;

namespace Portal.Api.Services;

public sealed class CatalogService
{
    private readonly IDbConnectionFactory _factory;
    public CatalogService(IDbConnectionFactory factory) => _factory = factory;

    public const string ManagePermission = "TEMPLATE.MANAGE";

    public async Task<IReadOnlyList<CatalogCategory>> GetCatalogAsync(int userId, bool canSeeAll)
    {
        using var conn = _factory.CreateMeta();
        var rows = await conn.QueryAsync(
            $"""
            SELECT c.CategoryId, c.CategoryName, c.Description, c.DisplayOrder,
                   t.TemplateId, t.TemplateCode, t.TemplateName, t.Description AS TplDescription,
                   t.AllowCopy, t.AllowExport, t.MaxRows,
                   (SELECT COUNT(*) FROM portal.ReportTemplateParameters p WHERE p.TemplateId = t.TemplateId) AS ParameterCount
            FROM portal.ReportTemplates t
            JOIN portal.ReportCategories c ON c.CategoryId = t.CategoryId AND c.IsDeleted = 0
            WHERE t.IsDeleted = 0 AND t.Status = N'Published'
              AND ({AccessPredicate("t")})
            ORDER BY c.DisplayOrder, c.CategoryName, t.TemplateName;
            """, new { UserId = userId, CanSeeAll = canSeeAll ? 1 : 0 });

        var categories = new List<CatalogCategory>();
        foreach (var g in rows.GroupBy(r => (int)r.CategoryId))
        {
            var first = g.First();
            var templates = g.Select(r => new CatalogTemplate(
                (int)r.TemplateId, (string)r.TemplateCode, (string)r.TemplateName, (string?)r.TplDescription,
                (bool)r.AllowCopy, (bool)r.AllowExport, (int)r.MaxRows, (int)r.ParameterCount)).ToList();
            categories.Add(new CatalogCategory(
                (int)first.CategoryId, (string)first.CategoryName, (string?)first.Description,
                (int)first.DisplayOrder, templates));
        }
        return categories;
    }

    public async Task<bool> CanAccessAsync(int userId, bool canSeeAll, int templateId)
    {
        using var conn = _factory.CreateMeta();
        var cnt = await conn.ExecuteScalarAsync<int>(
            $"""
            SELECT COUNT(1) FROM portal.ReportTemplates t
            WHERE t.TemplateId = @TemplateId AND t.IsDeleted = 0 AND t.Status = N'Published'
              AND ({AccessPredicate("t")});
            """, new { UserId = userId, CanSeeAll = canSeeAll ? 1 : 0, TemplateId = templateId });
        return cnt > 0;
    }

    public async Task<TemplateDetail?> GetTemplateDetailAsync(int templateId)
    {
        using var conn = _factory.CreateMeta();
        var t = await conn.QuerySingleOrDefaultAsync(
            """
            SELECT t.TemplateId, t.TemplateCode, t.TemplateName, t.Description, t.CategoryId,
                   c.CategoryName, t.AllowCopy, t.AllowExport, t.MaxRows, t.TimeoutSeconds
            FROM portal.ReportTemplates t
            JOIN portal.ReportCategories c ON c.CategoryId = t.CategoryId
            WHERE t.TemplateId = @TemplateId AND t.IsDeleted = 0;
            """, new { TemplateId = templateId });
        if (t is null) return null;

        var ps = await conn.QueryAsync(
            """
            SELECT ParameterId, ParameterName, DisplayName, DataType, InputType, IsRequired,
                   DefaultValue, DisplayOrder, MinValue, MaxValue, MaxLength, Options
            FROM portal.ReportTemplateParameters
            WHERE TemplateId = @TemplateId ORDER BY DisplayOrder, ParameterId;
            """, new { TemplateId = templateId });

        var parameters = ps.Select(p => new TemplateParameter(
            (int)p.ParameterId, (string)p.ParameterName, (string)p.DisplayName, (string)p.DataType,
            (string)p.InputType, (bool)p.IsRequired, (string?)p.DefaultValue, (int)p.DisplayOrder,
            (string?)p.MinValue, (string?)p.MaxValue, (int?)p.MaxLength, (string?)p.Options)).ToList();

        return new TemplateDetail((int)t.TemplateId, (string)t.TemplateCode, (string)t.TemplateName,
            (string?)t.Description, (int)t.CategoryId, (string)t.CategoryName,
            (bool)t.AllowCopy, (bool)t.AllowExport, (int)t.MaxRows, (int)t.TimeoutSeconds, parameters);
    }

    /// <summary>
    /// Điều kiện SQL: xem được báo cáo khi có quyền quản trị, HOẶC (được cấp trực tiếp / qua vai trò)
    /// VÀ không bị chặn riêng (IsDeny) cho người dùng đó. Chặn riêng ghi đè quyền theo vai trò.
    /// </summary>
    private static string AccessPredicate(string tplAlias) =>
        $"""
        @CanSeeAll = 1 OR (
            EXISTS (
                SELECT 1 FROM portal.ReportTemplatePermissions g
                LEFT JOIN portal.UserRoles ur ON ur.RoleId = g.RoleId AND ur.UserId = @UserId
                WHERE g.TemplateId = {tplAlias}.TemplateId
                  AND ((g.UserId = @UserId AND g.IsDeny = 0) OR (g.RoleId IS NOT NULL AND ur.UserId IS NOT NULL))
            )
            AND NOT EXISTS (
                SELECT 1 FROM portal.ReportTemplatePermissions d
                WHERE d.TemplateId = {tplAlias}.TemplateId AND d.UserId = @UserId AND d.IsDeny = 1
            )
        )
        """;
}
