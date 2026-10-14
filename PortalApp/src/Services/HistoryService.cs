using Dapper;
using Portal.Api.Infrastructure;
using Portal.Api.Models;

namespace Portal.Api.Services;

public sealed class HistoryService
{
    private readonly IDbConnectionFactory _factory;
    public HistoryService(IDbConnectionFactory factory) => _factory = factory;

    // Các kiểu sắp xếp được phép (whitelist -> tránh SQL injection qua tham số sort).
    private static readonly Dictionary<string, string> SortMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["started_desc"]  = "e.StartedAt DESC",              // mới nhất
        ["started_asc"]   = "e.StartedAt ASC",               // cũ nhất
        ["duration_desc"] = "e.DurationMs DESC, e.StartedAt DESC", // chạy lâu nhất
        ["duration_asc"]  = "e.DurationMs ASC, e.StartedAt DESC",  // chạy nhanh nhất
    };

    public Task<IReadOnlyList<HistoryItem>> GetMineAsync(int userId, int take = 100)
        => QueryAsync("WHERE e.UserId = @UserId", new { UserId = userId, Take = take }, "e.StartedAt DESC");

    /// <summary>Lịch sử toàn hệ thống có lọc theo tên người chạy, trạng thái, khoảng ngày và sắp xếp.</summary>
    public Task<IReadOnlyList<HistoryItem>> GetAllAsync(
        string? q = null, string? status = null, string? sort = null,
        DateTime? from = null, DateTime? to = null, int take = 200)
    {
        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(q))
            clauses.Add("(u.Username LIKE @Q OR u.FullName LIKE @Q)");
        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
            clauses.Add("e.Status = @Status");
        if (from.HasValue) clauses.Add("e.StartedAt >= @From");   // mốc UTC (client gửi theo múi giờ của họ)
        if (to.HasValue) clauses.Add("e.StartedAt < @To");        // @To = đầu ngày kế tiếp -> bao trọn ngày cuối
        var where = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
        var orderBy = SortMap.TryGetValue(sort ?? "started_desc", out var ob) ? ob : SortMap["started_desc"];
        return QueryAsync(where, new { Take = take, Q = $"%{q}%", Status = status, From = from, To = to }, orderBy);
    }

    private async Task<IReadOnlyList<HistoryItem>> QueryAsync(string where, object args, string orderBy)
    {
        using var conn = _factory.CreateMeta();
        var rows = await conn.QueryAsync<HistoryItem>(
            $"""
            SELECT TOP (@Take)
                   e.ExecutionId, e.TemplateId, t.TemplateCode, t.TemplateName,
                   u.Username, e.StartedAt, e.DurationMs, e.RowCountOut,
                   e.Status, e.IsExported, e.ErrorMessage
            FROM portal.QueryExecutions e
            JOIN portal.ReportTemplates t ON t.TemplateId = e.TemplateId
            JOIN portal.Users u ON u.UserId = e.UserId
            {where}
            ORDER BY {orderBy};
            """, args);
        return rows.ToList();
    }

    /// <summary>Xếp hạng người dùng theo số lần chạy báo cáo (nhiều nhất trước).</summary>
    public async Task<IReadOnlyList<TopUserStat>> GetTopUsersAsync(int take = 50)
    {
        using var conn = _factory.CreateMeta();
        var rows = await conn.QueryAsync<TopUserStat>(
            """
            SELECT TOP (@Take)
                   u.UserId, u.Username, u.FullName, u.Department,
                   COUNT(*) AS TotalRuns,
                   SUM(CASE WHEN e.Status = N'Success' THEN 1 ELSE 0 END) AS SuccessRuns,
                   SUM(CASE WHEN e.Status IN (N'Failed', N'Timeout', N'Cancelled') THEN 1 ELSE 0 END) AS FailedRuns,
                   MAX(e.StartedAt) AS LastRun
            FROM portal.QueryExecutions e
            JOIN portal.Users u ON u.UserId = e.UserId
            GROUP BY u.UserId, u.Username, u.FullName, u.Department
            ORDER BY TotalRuns DESC, LastRun DESC;
            """, new { Take = take });
        return rows.ToList();
    }

    /// <summary>Lấy tham số đã dùng của một lần chạy (để "chạy lại").</summary>
    public async Task<(int TemplateId, Dictionary<string, string?> Params)?> GetRerunAsync(long executionId, int userId, bool canSeeAll)
    {
        using var conn = _factory.CreateMeta();
        var head = await conn.QuerySingleOrDefaultAsync(
            "SELECT TemplateId, UserId FROM portal.QueryExecutions WHERE ExecutionId = @E;",
            new { E = executionId });
        if (head is null) return null;
        if (!canSeeAll && (int)head.UserId != userId) return null;

        var ps = await conn.QueryAsync(
            "SELECT ParameterName, ParameterValue FROM portal.QueryExecutionParameters WHERE ExecutionId = @E;",
            new { E = executionId });
        var dict = new Dictionary<string, string?>();
        foreach (var p in ps) dict[(string)p.ParameterName] = (string?)p.ParameterValue;
        return ((int)head.TemplateId, dict);
    }
}
