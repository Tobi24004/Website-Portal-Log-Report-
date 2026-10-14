using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portal.Api.Infrastructure;
using Portal.Api.Models;
using Portal.Api.Services;

namespace Portal.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize]
public sealed class AdminController : ControllerBase
{
    private readonly AdminService _admin;
    private readonly AuditService _audit;
    private readonly IEmailSender _email;

    public AdminController(AdminService admin, AuditService audit, IEmailSender email)
    {
        _admin = admin;
        _audit = audit;
        _email = email;
    }

    // ---- Users ----
    [HttpGet("users")]
    [Authorize(Policy = "USER.MANAGE")]
    public async Task<IActionResult> Users() => Ok(await _admin.GetUsersAsync());

    [HttpPost("users")]
    [Authorize(Policy = "USER.MANAGE")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { message = "Tên đăng nhập và mật khẩu là bắt buộc." });
        try
        {
            var id = await _admin.CreateUserAsync(req, User.GetUserId());
            await _audit.LogAsync(User.GetUserId(), "USER.CREATE", "User", id.ToString(), newValue: req.Username, clientIp: HttpContext.ClientIp());

            // Tự động gửi email chào mừng kèm mật khẩu tạm (nếu có email).
            var emailResult = new EmailResult(false, "None", "Tài khoản không có email.");
            if (!string.IsNullOrWhiteSpace(req.Email))
            {
                var (subject, html) = WelcomeEmail.Build(req.Username, req.FullName, req.Password, _email.LoginUrl);
                emailResult = await _email.SendAsync(req.Email!, req.FullName ?? req.Username, subject, html);
                await _audit.LogAsync(User.GetUserId(), "USER.WELCOME_EMAIL", "User", id.ToString(),
                    newValue: (emailResult.Sent ? "sent" : "failed") + ":" + emailResult.Mode, clientIp: HttpContext.ClientIp());
            }

            return Ok(new { userId = id, emailSent = emailResult.Sent, emailMode = emailResult.Mode, emailDetail = emailResult.Detail });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("users/bulk")]
    [Authorize(Policy = "USER.MANAGE")]
    public async Task<IActionResult> BulkImport([FromBody] BulkImportRequest req)
    {
        if (req.Users is not { Length: > 0 })
            return BadRequest(new { message = "Không có dòng dữ liệu nào để nhập." });
        if (req.Users.Length > 1000)
            return BadRequest(new { message = "Tối đa 1000 dòng mỗi lần nhập." });

        var outcome = await _admin.BulkCreateUsersAsync(req.Users, User.GetUserId());
        var created = outcome.Results.Count(r => r.Created);

        if (req.SendEmail)
            foreach (var w in outcome.ToEmail)
            {
                var (subject, html) = WelcomeEmail.Build(w.Username, w.FullName, w.Password, _email.LoginUrl);
                await _email.SendAsync(w.Email, w.FullName ?? w.Username, subject, html);
            }

        await _audit.LogAsync(User.GetUserId(), "USER.BULK_IMPORT", "User", null,
            newValue: $"created={created}, failed={req.Users.Length - created}, email={(req.SendEmail ? "on" : "off")}",
            clientIp: HttpContext.ClientIp());

        return Ok(new BulkImportResponse(created, req.Users.Length - created, outcome.Results));
    }

    [HttpGet("outbox")]
    [Authorize(Policy = "USER.MANAGE")]
    public IActionResult Outbox() => Ok(_email.RecentOutbox());

    [HttpPost("users/{id:int}/reset-password")]
    [Authorize(Policy = "USER.MANAGE")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
            return BadRequest(new { message = "Mật khẩu mới phải có ít nhất 6 ký tự." });
        await _admin.ResetPasswordAsync(id, req.NewPassword, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), "USER.RESET_PASSWORD", "User", id.ToString(), clientIp: HttpContext.ClientIp());
        return Ok(new { message = "Đã đặt lại mật khẩu." });
    }

    [HttpPost("users/{id:int}/active")]
    [Authorize(Policy = "USER.MANAGE")]
    public async Task<IActionResult> SetActive(int id, [FromBody] SetActiveRequest req)
    {
        await _admin.SetActiveAsync(id, req.IsActive, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), req.IsActive ? "USER.ACTIVATE" : "USER.DEACTIVATE", "User", id.ToString(), clientIp: HttpContext.ClientIp());
        return Ok(new { message = req.IsActive ? "Đã mở khóa tài khoản." : "Đã khóa tài khoản." });
    }

    [HttpGet("users/{id:int}/permissions")]
    [Authorize(Policy = "USER.MANAGE")]
    public async Task<IActionResult> GetUserPermissions(int id)
        => Ok(await _admin.GetUserPermissionsAsync(id));

    [HttpPut("users/{id:int}/permissions")]
    [Authorize(Policy = "USER.MANAGE")]
    public async Task<IActionResult> SetUserPermissions(int id, [FromBody] SetUserPermissionsRequest req)
    {
        await _admin.SetUserPermissionsAsync(id, req.TemplateIds ?? Array.Empty<int>(), User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), "USER.SET_PERMISSIONS", "User", id.ToString(),
            newValue: $"{(req.TemplateIds?.Length ?? 0)} báo cáo", clientIp: HttpContext.ClientIp());
        return Ok(new { message = "Đã cập nhật quyền truy cập báo cáo." });
    }

    // ---- Connections ----
    [HttpGet("connections")]
    [Authorize(Policy = "CONNECTION.MANAGE")]
    public async Task<IActionResult> Connections() => Ok(await _admin.GetConnectionsAsync());

    [HttpPost("connections/{id:int}/test")]
    [Authorize(Policy = "CONNECTION.MANAGE")]
    public async Task<IActionResult> TestConnection(int id)
    {
        var res = await _admin.TestConnectionAsync(id);
        await _audit.LogAsync(User.GetUserId(), "CONNECTION.TEST", "DatabaseConnection", id.ToString(),
            newValue: res.Ok ? "OK" : "FAILED", clientIp: HttpContext.ClientIp());
        return Ok(res);
    }

    // ---- Templates ----
    [HttpGet("templates")]
    [Authorize(Policy = "TEMPLATE.MANAGE")]
    public async Task<IActionResult> Templates() => Ok(await _admin.GetTemplatesAsync());

    [HttpGet("lookups")]
    [Authorize(Policy = "TEMPLATE.MANAGE")]
    public async Task<IActionResult> Lookups() => Ok(await _admin.GetLookupsAsync());

    [HttpGet("templates/{id:int}")]
    [Authorize(Policy = "TEMPLATE.MANAGE")]
    public async Task<IActionResult> TemplateFull(int id)
    {
        var t = await _admin.GetTemplateFullAsync(id);
        return t is null ? NotFound(new { message = "Không tìm thấy báo cáo." }) : Ok(t);
    }

    [HttpPost("templates/validate-sql")]
    [Authorize(Policy = "TEMPLATE.MANAGE")]
    public IActionResult ValidateSql([FromBody] SqlValidateRequest req)
    {
        var (ok, reason) = Portal.Api.Infrastructure.SqlGuard.Validate(req.Sql);
        return Ok(new SqlValidateResult(ok, reason));
    }

    [HttpPost("templates")]
    [Authorize(Policy = "TEMPLATE.MANAGE")]
    public async Task<IActionResult> CreateTemplate([FromBody] TemplateFormRequest req)
    {
        try
        {
            var id = await _admin.SaveTemplateAsync(null, req, User.GetUserId());
            await _audit.LogAsync(User.GetUserId(), "TEMPLATE.CREATE", "ReportTemplate", id.ToString(), newValue: req.TemplateCode, clientIp: HttpContext.ClientIp());
            return Ok(new { templateId = id });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("templates/{id:int}")]
    [Authorize(Policy = "TEMPLATE.MANAGE")]
    public async Task<IActionResult> UpdateTemplate(int id, [FromBody] TemplateFormRequest req)
    {
        try
        {
            await _admin.SaveTemplateAsync(id, req, User.GetUserId());
            await _audit.LogAsync(User.GetUserId(), "TEMPLATE.UPDATE", "ReportTemplate", id.ToString(), newValue: req.TemplateCode, clientIp: HttpContext.ClientIp());
            return Ok(new { templateId = id });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("templates/{id:int}")]
    [Authorize(Policy = "TEMPLATE.MANAGE")]
    public async Task<IActionResult> DeleteTemplate(int id)
    {
        await _admin.SoftDeleteTemplateAsync(id, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), "TEMPLATE.DELETE", "ReportTemplate", id.ToString(), clientIp: HttpContext.ClientIp());
        return Ok(new { message = "Đã xóa báo cáo." });
    }

    // ---- Audit ----
    [HttpGet("audit")]
    [Authorize(Policy = "AUDIT.VIEW")]
    public async Task<IActionResult> Audit() => Ok(await _admin.GetAuditAsync());
}
