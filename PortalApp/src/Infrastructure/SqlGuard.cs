using System.Text.RegularExpressions;

namespace Portal.Api.Infrastructure;

/// <summary>
/// "Lá chắn SQL": chỉ cho phép câu lệnh CHỈ ĐỌC (SELECT/WITH) chạy trên DB nguồn.
/// Chặn mọi từ khóa ghi/DDL/thực thi và các thủ đoạn nối lệnh (;, comment, xp_).
/// Đây là lớp phòng thủ bổ sung — giá trị tham số luôn được bind, không nối chuỗi.
/// </summary>
public static class SqlGuard
{
    // Các từ khóa nguy hiểm đứng ở đầu một statement.
    private static readonly string[] ForbiddenKeywords =
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE",
        "GRANT", "REVOKE", "DENY", "EXEC", "EXECUTE", "SP_", "XP_", "SHUTDOWN",
        "BACKUP", "RESTORE", "RECONFIGURE", "OPENROWSET", "OPENQUERY", "OPENDATASOURCE",
        "BULK", "WAITFOR", "DBCC", "KILL", "USE "
    };

    public static (bool Ok, string? Reason) Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return (false, "Câu lệnh SQL rỗng.");

        var stripped = StripCommentsAndStrings(sql);
        var normalized = stripped.Trim();

        // Phải bắt đầu bằng SELECT hoặc WITH (CTE).
        if (!Regex.IsMatch(normalized, @"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase))
            return (false, "Chỉ cho phép câu lệnh SELECT (hoặc WITH ... SELECT).");

        // Không cho nhiều statement (dấu ; ở giữa, trừ dấu ; cuối cùng).
        var trimmedSemis = normalized.TrimEnd().TrimEnd(';').TrimEnd();
        if (trimmedSemis.Contains(';'))
            return (false, "Không cho phép nhiều câu lệnh (ký tự ';').");

        var upper = " " + Regex.Replace(normalized, @"\s+", " ").ToUpperInvariant() + " ";
        foreach (var kw in ForbiddenKeywords)
        {
            // Bắt theo ranh giới từ để tránh false positive (vd: CREATED trong tên cột).
            var pattern = kw.EndsWith("_")
                ? $@"\b{Regex.Escape(kw)}"
                : $@"\b{Regex.Escape(kw.Trim())}\b";
            if (Regex.IsMatch(upper, pattern))
                return (false, $"Phát hiện từ khóa không được phép: '{kw.Trim()}'.");
        }

        return (true, null);
    }

    /// <summary>Loại bỏ comment (-- , /* */) và nội dung chuỗi 'N'...'' để kiểm tra từ khóa chính xác.</summary>
    private static string StripCommentsAndStrings(string sql)
    {
        // Bỏ block comment
        sql = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        // Bỏ line comment
        sql = Regex.Replace(sql, @"--[^\n]*", " ");
        // Thay nội dung literal chuỗi bằng rỗng để không bắt nhầm từ khóa trong text.
        sql = Regex.Replace(sql, @"N?'(?:''|[^'])*'", "''", RegexOptions.Singleline);
        return sql;
    }
}
