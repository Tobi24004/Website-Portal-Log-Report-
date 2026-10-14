using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace Portal.Api.Services;

public sealed record EmailResult(bool Sent, string Mode, string? Detail);
public sealed record SentEmail(DateTime SentAtUtc, string To, string Subject, string Mode, bool Sent, string? Detail, string BodyHtml);

public interface IEmailSender
{
    Task<EmailResult> SendAsync(string toEmail, string toName, string subject, string htmlBody);
    IReadOnlyList<SentEmail> RecentOutbox();
    string LoginUrl { get; }
}

/// <summary>
/// Gửi email. Ba chế độ (Portal:Email:Mode):
///   - Smtp    : gửi thật qua SMTP server (cấu hình Portal:Email:Smtp).
///   - Pickup  : KHÔNG cần server — ghi file .eml vào thư mục (demo/offline).
///   - Disabled: không gửi.
/// Mọi email đều được lưu vào "outbox" trong bộ nhớ (tối đa 50) để xem lại.
/// </summary>
public sealed class EmailSender : IEmailSender
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailSender> _logger;
    private readonly ConcurrentQueue<SentEmail> _outbox = new();
    private const int MaxOutbox = 50;

    public EmailSender(IConfiguration config, ILogger<EmailSender> logger)
    {
        _config = config;
        _logger = logger;
    }

    public string LoginUrl => _config["Portal:Email:LoginUrl"] ?? "http://localhost:8080";

    private string Mode => (_config["Portal:Email:Mode"] ?? "Pickup").Trim();

    public async Task<EmailResult> SendAsync(string toEmail, string toName, string subject, string htmlBody)
    {
        var mode = Mode;
        EmailResult result;
        try
        {
            result = mode.Equals("Smtp", StringComparison.OrdinalIgnoreCase)
                ? await SendSmtpAsync(toEmail, toName, subject, htmlBody)
                : mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
                    ? new EmailResult(false, "Disabled", "Email đang tắt.")
                    : SendPickup(toEmail, toName, subject, htmlBody);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gửi email tới {To} thất bại (mode {Mode}).", toEmail, mode);
            result = new EmailResult(false, mode, "Lỗi gửi: " + ex.Message);
        }

        Record(new SentEmail(DateTime.UtcNow, toEmail, subject, result.Mode, result.Sent, result.Detail, htmlBody));
        return result;
    }

    private MailMessage BuildMessage(string toEmail, string toName, string subject, string htmlBody)
    {
        var from = _config["Portal:Email:From"] ?? "no-reply@portal.local";
        var fromName = _config["Portal:Email:FromName"] ?? "Portal Log Report";
        var msg = new MailMessage
        {
            From = new MailAddress(from, fromName, Encoding.UTF8),
            Subject = subject,
            SubjectEncoding = Encoding.UTF8,
            Body = htmlBody,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = true
        };
        msg.To.Add(new MailAddress(toEmail, toName, Encoding.UTF8));
        return msg;
    }

    private EmailResult SendPickup(string toEmail, string toName, string subject, string htmlBody)
    {
        var dir = _config["Portal:Email:PickupDirectory"] ?? "/app/mail";
        Directory.CreateDirectory(dir);
        using var msg = BuildMessage(toEmail, toName, subject, htmlBody);
        using var client = new SmtpClient
        {
            DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
            PickupDirectoryLocation = dir
        };
        client.Send(msg);
        return new EmailResult(true, "Pickup", $"Đã ghi email .eml vào {dir} (chế độ demo, không cần SMTP server).");
    }

    private async Task<EmailResult> SendSmtpAsync(string toEmail, string toName, string subject, string htmlBody)
    {
        var host = _config["Portal:Email:Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Chưa cấu hình Portal:Email:Smtp:Host.");
        var port = int.TryParse(_config["Portal:Email:Smtp:Port"], out var p) ? p : 587;
        var user = _config["Portal:Email:Smtp:User"];
        var pass = _config["Portal:Email:Smtp:Password"];
        var useSsl = !string.Equals(_config["Portal:Email:Smtp:UseSsl"], "false", StringComparison.OrdinalIgnoreCase);

        using var msg = BuildMessage(toEmail, toName, subject, htmlBody);
        using var client = new SmtpClient(host, port) { EnableSsl = useSsl };
        if (!string.IsNullOrWhiteSpace(user))
            client.Credentials = new NetworkCredential(user, pass);
        await client.SendMailAsync(msg);
        return new EmailResult(true, "Smtp", $"Đã gửi qua {host}:{port}.");
    }

    private void Record(SentEmail mail)
    {
        _outbox.Enqueue(mail);
        while (_outbox.Count > MaxOutbox && _outbox.TryDequeue(out _)) { }
    }

    public IReadOnlyList<SentEmail> RecentOutbox() => _outbox.Reverse().ToList();
}

/// <summary>Soạn nội dung email chào mừng khi Admin tạo tài khoản.</summary>
public static class WelcomeEmail
{
    public static (string Subject, string Html) Build(string username, string? fullName, string tempPassword, string loginUrl)
    {
        var name = string.IsNullOrWhiteSpace(fullName) ? username : fullName!;
        var subject = "[Portal Log Report] Tài khoản của bạn đã được tạo";
        var html = $@"<!DOCTYPE html><html><body style=""font-family:Segoe UI,Arial,sans-serif;color:#1f2733"">
<div style=""max-width:560px;margin:0 auto;border:1px solid #e2e7ef;border-radius:12px;overflow:hidden"">
  <div style=""background:#2563eb;color:#fff;padding:18px 24px;font-size:18px;font-weight:700"">📊 Portal Log Report</div>
  <div style=""padding:24px"">
    <p>Xin chào <b>{Html(name)}</b>,</p>
    <p>Quản trị viên đã tạo cho bạn một tài khoản truy cập hệ thống <b>Portal Log Report</b>.
       Thông tin đăng nhập tạm thời của bạn:</p>
    <table style=""border-collapse:collapse;margin:14px 0"">
      <tr><td style=""padding:6px 12px;color:#6b7686"">Tên đăng nhập</td>
          <td style=""padding:6px 12px""><b>{Html(username)}</b></td></tr>
      <tr><td style=""padding:6px 12px;color:#6b7686"">Mật khẩu tạm</td>
          <td style=""padding:6px 12px""><b>{Html(tempPassword)}</b></td></tr>
    </table>
    <p><a href=""{Html(loginUrl)}"" style=""display:inline-block;background:#2563eb;color:#fff;
       text-decoration:none;padding:10px 20px;border-radius:8px;font-weight:600"">Đăng nhập ngay</a></p>
    <p style=""color:#b45309;background:#fef3c7;padding:10px 14px;border-radius:8px"">
       ⚠ Vì lý do bảo mật, vui lòng <b>đổi mật khẩu</b> ngay trong lần đăng nhập đầu tiên.
       Sau khi đổi, kể cả quản trị viên cũng không biết mật khẩu mới của bạn.</p>
    <p style=""color:#6b7686;font-size:12px"">Email tự động, vui lòng không trả lời.</p>
  </div>
</div></body></html>";
        return (subject, html);
    }

    private static string Html(string s) => System.Net.WebUtility.HtmlEncode(s);
}
