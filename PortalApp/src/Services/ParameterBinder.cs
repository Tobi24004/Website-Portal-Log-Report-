using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using System.Data;
using Portal.Api.Models;

namespace Portal.Api.Services;

/// <summary>Lỗi dữ liệu tham số do người dùng nhập — trả về 400 với thông báo thân thiện.</summary>
public sealed class ReportValidationException : Exception
{
    public ReportValidationException(string message) : base(message) { }
}

/// <summary>
/// Kiểm tra và ép kiểu giá trị tham số, rồi tạo SqlParameter bind an toàn
/// (KHÔNG bao giờ nối chuỗi vào câu lệnh SQL).
/// </summary>
public static class ParameterBinder
{
    public static (List<SqlParameter> SqlParams, List<(string Name, string? Value)> ForLog) Bind(
        IReadOnlyList<TemplateParameter> defs, IDictionary<string, string?> input)
    {
        var sqlParams = new List<SqlParameter>();
        var forLog = new List<(string, string?)>();
        // Lưu các giá trị ngày/giờ đã parse để kiểm tra chéo (từ ngày ≤ đến ngày).
        var dateValues = new Dictionary<string, (TemplateParameter Def, DateTime Value)>(StringComparer.OrdinalIgnoreCase);

        foreach (var def in defs.OrderBy(d => d.DisplayOrder))
        {
            // Chấp nhận key có hoặc không có '@'.
            var raw = GetValue(input, def.ParameterName);
            var trimmed = raw?.Trim();
            var isEmpty = string.IsNullOrWhiteSpace(trimmed);

            if (isEmpty)
            {
                if (def.IsRequired)
                    throw new ReportValidationException($"Tham số \"{def.DisplayName}\" là bắt buộc.");
                sqlParams.Add(NullParam(def));
                forLog.Add((def.ParameterName, null));
                continue;
            }

            var value = trimmed!;
            var converted = ConvertAndValidate(def, value);
            if (converted is DateTime dt)
                dateValues[def.ParameterName.TrimStart('@')] = (def, dt);
            var p = new SqlParameter(NormalizeName(def.ParameterName), converted) { SqlDbType = SqlType(def.DataType) };
            sqlParams.Add(p);
            forLog.Add((def.ParameterName, value));
        }

        ValidateDateRanges(dateValues);
        return (sqlParams, forLog);
    }

    /// <summary>Kiểm tra "từ ngày" không lớn hơn "đến ngày" (§5.8). Nhận diện cặp theo tiền tố Start/End, From/To.</summary>
    private static void ValidateDateRanges(Dictionary<string, (TemplateParameter Def, DateTime Value)> dates)
    {
        foreach (var kv in dates)
        {
            var name = kv.Key;
            string? endName = null;
            if (name.StartsWith("Start", StringComparison.OrdinalIgnoreCase))
                endName = "End" + name[5..];
            else if (name.StartsWith("From", StringComparison.OrdinalIgnoreCase))
                endName = "To" + name[4..];
            if (endName is null) continue;

            if (dates.TryGetValue(endName, out var end) && kv.Value.Value > end.Value)
                throw new ReportValidationException(
                    $"\"{kv.Value.Def.DisplayName}\" không được lớn hơn \"{end.Def.DisplayName}\".");
        }
    }

    private static string? GetValue(IDictionary<string, string?> input, string paramName)
    {
        if (input.TryGetValue(paramName, out var v)) return v;
        var noAt = paramName.TrimStart('@');
        if (input.TryGetValue(noAt, out v)) return v;
        if (input.TryGetValue("@" + noAt, out v)) return v;
        return null;
    }

    private static string NormalizeName(string name) => name.StartsWith('@') ? name : "@" + name;

    private static SqlParameter NullParam(TemplateParameter def)
        => new(NormalizeName(def.ParameterName), DBNull.Value) { SqlDbType = SqlType(def.DataType) };

    private static SqlDbType SqlType(string dataType) => dataType switch
    {
        "Integer" => SqlDbType.Int,
        "Decimal" => SqlDbType.Decimal,
        "Date" => SqlDbType.Date,
        "DateTime" => SqlDbType.DateTime2,
        "Checkbox" => SqlDbType.Bit,
        _ => SqlDbType.NVarChar          // Text, Dropdown
    };

    private static object ConvertAndValidate(TemplateParameter def, string value)
    {
        switch (def.DataType)
        {
            case "Integer":
            {
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    throw new ReportValidationException($"Tham số \"{def.DisplayName}\" phải là số nguyên.");
                CheckRangeNumeric(def, n);
                return (int)n;
            }
            case "Decimal":
            {
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                    throw new ReportValidationException($"Tham số \"{def.DisplayName}\" phải là số.");
                CheckRangeNumeric(def, d);
                return d;
            }
            case "Date":
            case "DateTime":
            {
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    throw new ReportValidationException($"Tham số \"{def.DisplayName}\" không phải ngày/giờ hợp lệ.");
                CheckRangeDate(def, dt);
                return dt;
            }
            case "Checkbox":
            {
                if (bool.TryParse(value, out var b)) return b;
                if (value is "1" or "on" or "yes") return true;
                if (value is "0" or "off" or "no") return false;
                throw new ReportValidationException($"Tham số \"{def.DisplayName}\" phải là true/false.");
            }
            case "Dropdown":
                ValidateDropdown(def, value);
                CheckMaxLength(def, value);
                return value;
            default: // Text
                CheckMaxLength(def, value);
                return value;
        }
    }

    private static void CheckMaxLength(TemplateParameter def, string value)
    {
        if (def.MaxLength is > 0 && value.Length > def.MaxLength)
            throw new ReportValidationException($"Tham số \"{def.DisplayName}\" vượt quá độ dài tối đa {def.MaxLength} ký tự.");
    }

    private static void CheckRangeNumeric(TemplateParameter def, decimal n)
    {
        if (decimal.TryParse(def.MinValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var min) && n < min)
            throw new ReportValidationException($"Tham số \"{def.DisplayName}\" phải ≥ {def.MinValue}.");
        if (decimal.TryParse(def.MaxValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var max) && n > max)
            throw new ReportValidationException($"Tham số \"{def.DisplayName}\" phải ≤ {def.MaxValue}.");
    }

    private static void CheckRangeDate(TemplateParameter def, DateTime dt)
    {
        if (DateTime.TryParse(def.MinValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var min) && dt < min)
            throw new ReportValidationException($"Tham số \"{def.DisplayName}\" phải từ {def.MinValue} trở đi.");
        if (DateTime.TryParse(def.MaxValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var max) && dt > max)
            throw new ReportValidationException($"Tham số \"{def.DisplayName}\" phải trước {def.MaxValue}.");
    }

    private static void ValidateDropdown(TemplateParameter def, string value)
    {
        if (string.IsNullOrWhiteSpace(def.Options)) return;
        try
        {
            using var doc = JsonDocument.Parse(def.Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var candidate = el.ValueKind == JsonValueKind.Object
                    ? (el.TryGetProperty("value", out var v) ? v.ToString() : null)
                    : el.ToString();
                if (candidate == value) return;
            }
            throw new ReportValidationException($"Giá trị tham số \"{def.DisplayName}\" không nằm trong danh sách cho phép.");
        }
        catch (JsonException)
        {
            // Options không phải JSON hợp lệ → bỏ qua kiểm tra danh sách.
        }
    }
}
