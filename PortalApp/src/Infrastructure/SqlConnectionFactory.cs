using Microsoft.Data.SqlClient;

namespace Portal.Api.Infrastructure;

/// <summary>
/// Tạo kết nối tới metadata DB (DBLogReportPortal) và tới DB nguồn (từ bản ghi
/// DatabaseConnections). Mật khẩu DB nguồn được giải mã ngay trước khi dùng.
/// </summary>
public interface IDbConnectionFactory
{
    SqlConnection CreateMeta();
    SqlConnection CreateSource(SourceConnectionInfo info, string decryptedPassword);
}

public sealed record SourceConnectionInfo(
    int ConnectionId,
    string ServerHost,
    int? Port,
    string DatabaseName,
    string Username,
    int ConnectionTimeout,
    int CommandTimeout);

public sealed class SqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _metaConnString;
    private readonly string? _sourceHostOverride;

    public SqlConnectionFactory(IConfiguration config)
    {
        _metaConnString = config["Portal:MetaConnectionString"]
            ?? throw new InvalidOperationException("Thiếu cấu hình Portal:MetaConnectionString.");
        _sourceHostOverride = config["Portal:SourceHostOverride"];
    }

    public SqlConnection CreateMeta() => new(_metaConnString);

    public SqlConnection CreateSource(SourceConnectionInfo info, string decryptedPassword)
    {
        // Khi chạy trong container, 'localhost' không trỏ tới SQL Server của host.
        // Cho phép ghi đè host để kết nối được (ví dụ host.docker.internal).
        var host = info.ServerHost;
        if (!string.IsNullOrWhiteSpace(_sourceHostOverride) &&
            (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             host == "127.0.0.1" || host == "." || host == "(local)"))
        {
            host = _sourceHostOverride!;
        }

        var dataSource = info.Port is > 0 ? $"{host},{info.Port}" : host;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = info.DatabaseName,
            UserID = info.Username,
            Password = decryptedPassword,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = info.ConnectionTimeout <= 0 ? 15 : info.ConnectionTimeout,
            // Thực thi báo cáo chỉ đọc; tài khoản nguồn cũng bị DENY ghi (phòng thủ nhiều lớp).
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "PortalLogReport"
        };
        return new SqlConnection(builder.ConnectionString);
    }
}
