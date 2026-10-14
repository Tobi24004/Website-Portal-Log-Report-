using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portal.Api.Infrastructure;
using Portal.Api.Services;

namespace Portal.Api.Controllers;

[ApiController]
[Route("api/catalog")]
[Authorize]
public sealed class CatalogController : ControllerBase
{
    private readonly CatalogService _catalog;
    public CatalogController(CatalogService catalog) => _catalog = catalog;

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var canSeeAll = User.HasPermission(CatalogService.ManagePermission);
        var cats = await _catalog.GetCatalogAsync(User.GetUserId(), canSeeAll);
        return Ok(cats);
    }

    [HttpGet("templates/{id:int}")]
    public async Task<IActionResult> GetTemplate(int id)
    {
        var canSeeAll = User.HasPermission(CatalogService.ManagePermission);
        if (!await _catalog.CanAccessAsync(User.GetUserId(), canSeeAll, id))
            return StatusCode(403, new { message = "Bạn không có quyền truy cập báo cáo này." });

        var detail = await _catalog.GetTemplateDetailAsync(id);
        return detail is null ? NotFound(new { message = "Không tìm thấy báo cáo." }) : Ok(detail);
    }
}
