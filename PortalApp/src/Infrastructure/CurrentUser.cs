using System.Security.Claims;

namespace Portal.Api.Infrastructure;

public static class CurrentUser
{
    public static int GetUserId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("uid");
        return int.TryParse(raw, out var id) ? id : 0;
    }

    public static string GetUsername(this ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.Name) ?? user.Identity?.Name ?? "unknown";

    public static bool HasPermission(this ClaimsPrincipal user, string permission)
        => user.Claims.Any(c => c.Type == "perm" && c.Value == permission);

    public static string? ClientIp(this HttpContext? ctx)
    {
        if (ctx is null) return null;
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var fwd) && fwd.Count > 0)
            ip = fwd.ToString().Split(',')[0].Trim();
        return ip;
    }
}
