using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Portal.Api.Infrastructure;
using Portal.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Ẩn header "Server: Kestrel" (giảm lộ thông tin nền tảng).
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// ---- Data Protection (mã hóa mật khẩu kết nối); khóa lưu bền ở /app/keys ----
var keysDir = new DirectoryInfo("/app/keys");
if (!keysDir.Exists) keysDir.Create();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(keysDir)
    .SetApplicationName("PortalLogReport");

// ---- DI ----
builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
builder.Services.AddSingleton<IEmailSender, EmailSender>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<HistoryService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<SeedService>();

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// ---- AuthN (JWT) ----
var tokenService = new TokenService(builder.Configuration);
builder.Services.AddSingleton(tokenService);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = tokenService.ValidationParameters;
    });

// ---- AuthZ (policy theo mã quyền) ----
string[] permissions =
{
    "USER.MANAGE", "ROLE.MANAGE", "CONNECTION.MANAGE", "TEMPLATE.MANAGE",
    "REPORT.RUN", "REPORT.EXPORT", "HISTORY.VIEW.ALL", "AUDIT.VIEW"
};
builder.Services.AddAuthorization(options =>
{
    foreach (var perm in permissions)
        options.AddPolicy(perm, policy => policy.RequireClaim("perm", perm));
});

// ---- Rate limiting cho đăng nhập (chống brute-force dồn dập theo IP) ----
var loginPermit = builder.Configuration.GetValue("Portal:Security:LoginRatePerMinute", 20);
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", httpCtx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpCtx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = loginPermit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.OnRejected = async (ctx, token) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await ctx.HttpContext.Response.WriteAsJsonAsync(new
        {
            message = "Quá nhiều yêu cầu đăng nhập. Vui lòng thử lại sau ít phút."
        }, token);
    };
});

var app = builder.Build();

// ---- Security headers cho mọi response (kể cả tĩnh & lỗi) ----
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";             // chặn MIME sniffing
    h["X-Frame-Options"] = "DENY";                        // chống clickjacking
    h["Referrer-Policy"] = "no-referrer";
    h["X-XSS-Protection"] = "0";                          // theo khuyến nghị hiện đại (dựa vào CSP)
    h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    h["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

// ---- Xử lý lỗi tập trung: trả JSON thân thiện, ẩn stack trace ở Production ----
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var feat = ctx.Features.Get<IExceptionHandlerFeature>();
    var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalError");
    logger.LogError(feat?.Error, "Unhandled exception trên {Path}", ctx.Request.Path);

    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    var message = app.Environment.IsDevelopment() && feat?.Error is not null
        ? feat.Error.Message
        : "Đã xảy ra lỗi hệ thống. Vui lòng thử lại hoặc liên hệ quản trị.";
    await ctx.Response.WriteAsJsonAsync(new { message });
}));

// Phục vụ SPA tĩnh từ wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Bắt buộc đổi mật khẩu lần đầu: nếu token có claim "mustchange", chặn mọi API
// (trừ me/logout/change-password) cho tới khi người dùng đổi mật khẩu.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true &&
        ctx.User.FindFirst("mustchange")?.Value == "1")
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        bool allowed =
            path.StartsWith("/api/auth/change-password", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/auth/me", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/auth/logout", StringComparison.OrdinalIgnoreCase);
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !allowed)
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsJsonAsync(new
            {
                message = "Bạn phải đổi mật khẩu trước khi tiếp tục sử dụng hệ thống.",
                mustChangePassword = true
            });
            return;
        }
    }
    await next();
});

app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", timeUtc = DateTime.UtcNow }));

// SPA fallback: mọi route không phải /api trả về index.html.
app.MapFallbackToFile("index.html");

// ---- Seed khi khởi động (băm mật khẩu demo, mã hóa mật khẩu kết nối) ----
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await scope.ServiceProvider.GetRequiredService<SeedService>().RunAsync();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Seed khởi động thất bại (ứng dụng vẫn chạy). Kiểm tra kết nối metadata DB.");
    }
}

app.Run();
