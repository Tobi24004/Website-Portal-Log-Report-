using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portal.Api.Infrastructure;
using Portal.Api.Models;
using Portal.Api.Services;

namespace Portal.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly AuditService _audit;

    public AuthController(AuthService auth, AuditService audit)
    {
        _auth = auth;
        _audit = audit;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { message = "Vui lòng nhập tên đăng nhập và mật khẩu." });

        var result = await _auth.LoginAsync(req.Username.Trim(), req.Password, HttpContext.ClientIp());
        if (result is null)
            return Unauthorized(new { message = "Sai tên đăng nhập hoặc mật khẩu, hoặc tài khoản đã bị khóa." });

        return Ok(result);
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var info = await _auth.GetMeAsync(User.GetUserId());
        return info is null ? Unauthorized() : Ok(info);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await _audit.LogAsync(User.GetUserId(), "AUTH.LOGOUT", "User", User.GetUserId().ToString(), clientIp: HttpContext.ClientIp());
        return Ok(new { message = "Đã đăng xuất." });
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
            return BadRequest(new { message = "Mật khẩu mới phải có ít nhất 6 ký tự." });
        if (req.NewPassword == req.CurrentPassword)
            return BadRequest(new { message = "Mật khẩu mới phải khác mật khẩu hiện tại." });

        var result = await _auth.ChangePasswordAsync(User.GetUserId(), req.CurrentPassword, req.NewPassword);
        if (result is null) return BadRequest(new { message = "Mật khẩu hiện tại không đúng." });

        await _audit.LogAsync(User.GetUserId(), "AUTH.CHANGE_PASSWORD", "User", User.GetUserId().ToString(), clientIp: HttpContext.ClientIp());
        // Trả token mới (đã xóa cờ bắt buộc đổi) để client tiếp tục phiên.
        return Ok(result);
    }
}
