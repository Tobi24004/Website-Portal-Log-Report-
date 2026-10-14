using Dapper;
using Portal.Api.Infrastructure;

namespace Portal.Api.Services;

/// <summary>Ghi Audit Log cho các hành động quản trị. KHÔNG ghi mật khẩu/connection string.</summary>
public sealed class AuditService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IDbConnectionFactory factory, ILogger<AuditService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task LogAsync(int? userId, string action, string? targetObject = null,
        string? targetId = null, string? oldValue = null, string? newValue = null, string? clientIp = null)
    {
        try
        {
            using var conn = _factory.CreateMeta();
            await conn.ExecuteAsync(
                """
                INSERT INTO portal.AuditLogs (UserId, Action, TargetObject, TargetId, OldValue, NewValue, ClientIP)
                VALUES (@UserId, @Action, @TargetObject, @TargetId, @OldValue, @NewValue, @ClientIP);
                """,
                new { UserId = userId, Action = action, TargetObject = targetObject, TargetId = targetId,
                      OldValue = oldValue, NewValue = newValue, ClientIP = clientIp });
        }
        catch (Exception ex)
        {
            // Audit không được làm hỏng luồng chính.
            _logger.LogWarning(ex, "Ghi audit log thất bại cho action {Action}", action);
        }
    }
}
