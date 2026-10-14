using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portal.Api.Infrastructure;
using Portal.Api.Services;

namespace Portal.Api.Controllers;

[ApiController]
[Route("api/history")]
[Authorize]
public sealed class HistoryController : ControllerBase
{
    private readonly HistoryService _history;
    public HistoryController(HistoryService history) => _history = history;

    [HttpGet("my")]
    public async Task<IActionResult> My() => Ok(await _history.GetMineAsync(User.GetUserId()));

    [HttpGet("all")]
    public async Task<IActionResult> All([FromQuery] string? q, [FromQuery] string? status, [FromQuery] string? sort)
    {
        if (!User.HasPermission("HISTORY.VIEW.ALL"))
            return StatusCode(403, new { message = "Bạn không có quyền xem lịch sử toàn hệ thống." });
        return Ok(await _history.GetAllAsync(q, status, sort));
    }

    [HttpGet("stats/top-users")]
    public async Task<IActionResult> TopUsers()
    {
        if (!User.HasPermission("HISTORY.VIEW.ALL"))
            return StatusCode(403, new { message = "Bạn không có quyền xem thống kê toàn hệ thống." });
        return Ok(await _history.GetTopUsersAsync());
    }

    [HttpGet("{executionId:long}/rerun")]
    public async Task<IActionResult> Rerun(long executionId)
    {
        var canSeeAll = User.HasPermission("HISTORY.VIEW.ALL");
        var res = await _history.GetRerunAsync(executionId, User.GetUserId(), canSeeAll);
        if (res is null) return NotFound(new { message = "Không tìm thấy lần chạy hoặc không có quyền." });
        return Ok(new { templateId = res.Value.TemplateId, parameters = res.Value.Params });
    }
}
