using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portal.Api.Infrastructure;
using Portal.Api.Models;
using Portal.Api.Services;

namespace Portal.Api.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public sealed class ReportsController : ControllerBase
{
    private readonly CatalogService _catalog;
    private readonly ReportService _reports;
    private readonly ExportService _export;
    private readonly AuditService _audit;

    public ReportsController(CatalogService catalog, ReportService reports, ExportService export, AuditService audit)
    {
        _catalog = catalog;
        _reports = reports;
        _export = export;
        _audit = audit;
    }

    [HttpPost("{id:int}/run")]
    public async Task<IActionResult> Run(int id, [FromBody] RunRequest req)
    {
        var userId = User.GetUserId();
        var canSeeAll = User.HasPermission(CatalogService.ManagePermission);
        if (!await _catalog.CanAccessAsync(userId, canSeeAll, id))
            return StatusCode(403, new { message = "Bạn không có quyền chạy báo cáo này." });

        try
        {
            var data = await _reports.ExecuteAsync(userId, id, req.Parameters ?? new(), HttpContext.ClientIp());
            return Ok(new RunResult(data.ExecutionId, data.Columns, data.Rows, data.Rows.Count,
                data.Truncated, data.DurationMs, data.Template.AllowCopy, data.Template.AllowExport));
        }
        catch (ReportValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:int}/export")]
    public async Task<IActionResult> Export(int id, [FromBody] RunRequest req)
    {
        var userId = User.GetUserId();
        var canSeeAll = User.HasPermission(CatalogService.ManagePermission);
        if (!await _catalog.CanAccessAsync(userId, canSeeAll, id))
            return StatusCode(403, new { message = "Bạn không có quyền xuất báo cáo này." });

        try
        {
            var data = await _reports.ExecuteAsync(userId, id, req.Parameters ?? new(), HttpContext.ClientIp());
            if (!data.Template.AllowExport)
                return StatusCode(403, new { message = "Báo cáo này không cho phép xuất Excel." });

            var file = await _export.BuildXlsxAndLogAsync(userId, User.GetUsername(), data);
            await _reports.MarkExportedAsync(data.ExecutionId);
            await _audit.LogAsync(userId, "REPORT.EXPORT", "ReportTemplate", id.ToString(),
                newValue: file.FileName, clientIp: HttpContext.ClientIp());

            return File(file.Content,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.FileName);
        }
        catch (ReportValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
