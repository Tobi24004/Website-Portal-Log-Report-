using Dapper;
using Portal.Api.Infrastructure;
using Portal.Api.Models;

namespace Portal.Api.Services;

public sealed class HistoryService
{
    private readonly IDbConnectionFactory _factory;
    public HistoryService(IDbConnectionFactory factory) => _factory = factory;

    public Task<IReadOnlyList<HistoryItem>> GetMineAsync(int userId, int take = 100)
        => QueryAsync("WHERE e.UserId = @UserId", new { UserId = userId, Take = take }, take);

    public Task<IReadOnlyList<HistoryItem>> GetAllAsync(int take = 200)
        => QueryAsync("", new { Take = take }, take);

    private async Task<IReadOnlyList<HistoryItem>> QueryAsync(string where, object args, int take)
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
            ORDER BY e.StartedAt DESC;
            """, args);
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
