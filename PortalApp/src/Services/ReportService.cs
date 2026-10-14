using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Portal.Api.Infrastructure;
using Portal.Api.Models;

namespace Portal.Api.Services;

public sealed record ExecutionData(
    long ExecutionId, IReadOnlyList<string> Columns, List<object?[]> Rows,
    bool Truncated, int DurationMs, TemplateDetail Template,
    IReadOnlyList<(string Name, string? Value)> Parameters);

public sealed class ReportService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ISecretProtector _protector;
    private readonly CatalogService _catalog;
    private readonly ILogger<ReportService> _logger;

    public ReportService(IDbConnectionFactory factory, ISecretProtector protector,
        CatalogService catalog, ILogger<ReportService> logger)
    {
        _factory = factory;
        _protector = protector;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<ExecutionData> ExecuteAsync(
        int userId, int templateId, IDictionary<string, string?> inputParams, string? clientIp)
    {
        var detail = await _catalog.GetTemplateDetailAsync(templateId)
            ?? throw new ReportValidationException("Không tìm thấy báo cáo.");

        // 1) Tải SqlCommand + thông tin connection nguồn.
        var meta = await LoadExecMetaAsync(templateId);

        // 2) Lá chắn SQL (phòng thủ bổ sung — template do admin tạo).
        var (ok, reason) = SqlGuard.Validate(meta.SqlCommand);
        if (!ok)
            throw new ReportValidationException($"Câu lệnh báo cáo bị từ chối bởi SQL Guard: {reason}");

        // 3) Kiểm tra + bind tham số (an toàn trước SQL injection).
        var (sqlParams, forLog) = ParameterBinder.Bind(detail.Parameters, inputParams);

        // 4) Ghi bản ghi thực thi trạng thái Running.
        long execId = await InsertExecutionAsync(userId, templateId, meta.Connection.ConnectionId, clientIp);
        await InsertExecParamsAsync(execId, forLog);

        var sw = Stopwatch.StartNew();
        try
        {
            var password = _protector.Decrypt(meta.PasswordEncrypted);
            await using var conn = _factory.CreateSource(meta.Connection, password);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(meta.SqlCommand, conn)
            {
                CommandTimeout = detail.TimeoutSeconds <= 0 ? 30 : detail.TimeoutSeconds
            };
            foreach (var p in sqlParams) cmd.Parameters.Add(p);

            await using var reader = await cmd.ExecuteReaderAsync();
            var columns = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++) columns[i] = reader.GetName(i);

            var rows = new List<object?[]>();
            bool truncated = false;
            int cap = detail.MaxRows <= 0 ? 10000 : detail.MaxRows;
            while (await reader.ReadAsync())
            {
                if (rows.Count >= cap) { truncated = true; break; }
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = Normalize(reader.GetValue(i));
                rows.Add(row);
            }
            sw.Stop();

            await UpdateExecutionAsync(execId, "Success", (int)sw.ElapsedMilliseconds, rows.Count, null);
            return new ExecutionData(execId, columns, rows, truncated, (int)sw.ElapsedMilliseconds, detail, forLog);
        }
        catch (SqlException ex) when (ex.Number == -2)   // command timeout
        {
            sw.Stop();
            await UpdateExecutionAsync(execId, "Timeout", (int)sw.ElapsedMilliseconds, null, "Truy vấn vượt quá thời gian cho phép.");
            throw new ReportValidationException("Báo cáo chạy quá thời gian cho phép (timeout). Hãy thu hẹp khoảng lọc và thử lại.");
        }
        catch (SqlException ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Lỗi thực thi báo cáo template {TemplateId}", templateId);
            await UpdateExecutionAsync(execId, "Failed", (int)sw.ElapsedMilliseconds, null, ex.Message);
            throw new ReportValidationException("Không thể chạy báo cáo trên cơ sở dữ liệu nguồn. Vui lòng kiểm tra tham số hoặc liên hệ quản trị.");
        }
    }

    private static object? Normalize(object value)
    {
        if (value is DBNull) return null;
        if (value is byte[] bytes) return "0x" + Convert.ToHexString(bytes);
        return value;
    }

    private sealed record ExecMeta(string SqlCommand, string PasswordEncrypted, SourceConnectionInfo Connection);

    private async Task<ExecMeta> LoadExecMetaAsync(int templateId)
    {
        using var conn = _factory.CreateMeta();
        var row = await conn.QuerySingleOrDefaultAsync(
            """
            SELECT t.SqlCommand,
                   dc.ConnectionId, dc.ServerHost, dc.Port, dc.DatabaseName, dc.Username,
                   dc.PasswordEncrypted, dc.ConnectionTimeout, dc.CommandTimeout
            FROM portal.ReportTemplates t
            JOIN portal.DatabaseConnections dc ON dc.ConnectionId = t.ConnectionId
            WHERE t.TemplateId = @TemplateId AND t.IsDeleted = 0 AND dc.IsDeleted = 0 AND dc.IsActive = 1;
            """, new { TemplateId = templateId });
        if (row is null)
            throw new ReportValidationException("Báo cáo chưa gắn kết nối hợp lệ. Liên hệ quản trị.");

        var info = new SourceConnectionInfo((int)row.ConnectionId, (string)row.ServerHost,
            (int?)row.Port, (string)row.DatabaseName, (string)row.Username,
            (int)row.ConnectionTimeout, (int)row.CommandTimeout);
        return new ExecMeta((string)row.SqlCommand, (string)row.PasswordEncrypted, info);
    }

    private async Task<long> InsertExecutionAsync(int userId, int templateId, int connId, string? clientIp)
    {
        using var conn = _factory.CreateMeta();
        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO portal.QueryExecutions (UserId, TemplateId, ConnectionId, Status, ClientIP)
            OUTPUT INSERTED.ExecutionId
            VALUES (@UserId, @TemplateId, @ConnectionId, N'Running', @ClientIP);
            """, new { UserId = userId, TemplateId = templateId, ConnectionId = connId, ClientIP = clientIp });
    }

    private async Task InsertExecParamsAsync(long execId, IReadOnlyList<(string Name, string? Value)> ps)
    {
        if (ps.Count == 0) return;
        using var conn = _factory.CreateMeta();
        foreach (var (name, value) in ps)
            await conn.ExecuteAsync(
                "INSERT INTO portal.QueryExecutionParameters (ExecutionId, ParameterName, ParameterValue) VALUES (@E, @N, @V);",
                new { E = execId, N = name, V = value });
    }

    private async Task UpdateExecutionAsync(long execId, string status, int durationMs, int? rowCount, string? error)
    {
        using var conn = _factory.CreateMeta();
        await conn.ExecuteAsync(
            """
            UPDATE portal.QueryExecutions
            SET Status = @Status, CompletedAt = SYSUTCDATETIME(), DurationMs = @Duration,
                RowCountOut = @RowCount, ErrorMessage = @Error
            WHERE ExecutionId = @ExecutionId;
            """, new { Status = status, Duration = durationMs, RowCount = rowCount, Error = error, ExecutionId = execId });
    }

    public async Task MarkExportedAsync(long execId)
    {
        using var conn = _factory.CreateMeta();
        await conn.ExecuteAsync("UPDATE portal.QueryExecutions SET IsExported = 1 WHERE ExecutionId = @E;", new { E = execId });
    }
}
