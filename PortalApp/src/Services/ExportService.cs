using ClosedXML.Excel;
using Dapper;
using Portal.Api.Infrastructure;

namespace Portal.Api.Services;

public sealed class ExportService
{
    private readonly IDbConnectionFactory _factory;
    public ExportService(IDbConnectionFactory factory) => _factory = factory;

    public sealed record ExportFile(byte[] Content, string FileName, int RowCount);

    public async Task<ExportFile> BuildXlsxAndLogAsync(int userId, string username, ExecutionData data)
    {
        using var wb = new XLWorkbook();

        // ---- Sheet Data ----
        var ws = wb.AddWorksheet("Data");
        for (int c = 0; c < data.Columns.Count; c++)
            ws.Cell(1, c + 1).Value = data.Columns[c];
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);

        for (int r = 0; r < data.Rows.Count; r++)
        {
            var row = data.Rows[r];
            for (int c = 0; c < row.Length; c++)
                ws.Cell(r + 2, c + 1).Value = ToCellValue(row[c]);
        }
        if (data.Columns.Count > 0)
            ws.Range(1, 1, Math.Max(1, data.Rows.Count + 1), data.Columns.Count).SetAutoFilter();
        ws.Columns().AdjustToContents();

        // ---- Sheet Parameters ----
        var wp = wb.AddWorksheet("Parameters");
        wp.Cell(1, 1).Value = "Báo cáo";
        wp.Cell(1, 2).Value = data.Template.TemplateName;
        wp.Cell(2, 1).Value = "Mã báo cáo";
        wp.Cell(2, 2).Value = data.Template.TemplateCode;
        wp.Cell(3, 1).Value = "Người thực hiện";
        wp.Cell(3, 2).Value = username;
        wp.Cell(4, 1).Value = "Thời điểm xuất (UTC)";
        wp.Cell(4, 2).Value = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        wp.Cell(5, 1).Value = "Số dòng";
        wp.Cell(5, 2).Value = data.Rows.Count;
        wp.Cell(6, 1).Value = "Thời gian chạy (ms)";
        wp.Cell(6, 2).Value = data.DurationMs;
        wp.Cell(8, 1).Value = "Tham số";
        wp.Cell(8, 2).Value = "Giá trị";
        wp.Range(8, 1, 8, 2).Style.Font.Bold = true;
        int pr = 9;
        foreach (var (name, value) in data.Parameters)
        {
            wp.Cell(pr, 1).Value = name;
            wp.Cell(pr, 2).Value = value ?? "(trống)";
            pr++;
        }
        wp.Column(1).Style.Font.Bold = true;
        wp.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var bytes = ms.ToArray();

        var fileName = $"{data.Template.TemplateCode}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx";
        await LogExportAsync(userId, data.ExecutionId, fileName, data.Rows.Count, bytes.LongLength);
        return new ExportFile(bytes, fileName, data.Rows.Count);
    }

    private static XLCellValue ToCellValue(object? value) => value switch
    {
        null => Blank.Value,
        bool b => b,
        byte or sbyte or short or ushort or int or uint or long or ulong
            => Convert.ToDouble(value),
        float or double or decimal => Convert.ToDouble(value),
        DateTime dt => dt,
        _ => value.ToString() ?? string.Empty
    };

    private async Task LogExportAsync(int userId, long execId, string fileName, int rowCount, long fileSize)
    {
        using var conn = _factory.CreateMeta();
        await conn.ExecuteAsync(
            """
            INSERT INTO portal.ExportHistories (ExecutionId, UserId, FileName, Format, RowCountOut, FileSize)
            VALUES (@ExecutionId, @UserId, @FileName, N'xlsx', @RowCount, @FileSize);
            """, new { ExecutionId = execId, UserId = userId, FileName = fileName, RowCount = rowCount, FileSize = fileSize });
    }
}
