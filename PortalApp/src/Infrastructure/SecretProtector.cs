using Microsoft.AspNetCore.DataProtection;

namespace Portal.Api.Infrastructure;

/// <summary>
/// Mã hóa/giải mã (hai chiều) mật khẩu kết nối DB nguồn bằng ASP.NET Core Data
/// Protection. Khóa được lưu bền ở /app/keys nên dữ liệu vẫn giải mã được sau
/// khi container khởi động lại. KHÔNG bao giờ lưu plaintext vào DB.
/// </summary>
public interface ISecretProtector
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
    bool IsEncrypted(string? value);
}

public sealed class SecretProtector : ISecretProtector
{
    private const string Prefix = "enc:v1:";
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("portal.connection.password.v1");

    public string Encrypt(string plaintext) => Prefix + _protector.Protect(plaintext);

    public string Decrypt(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
            throw new InvalidOperationException("Chuỗi mật khẩu rỗng.");
        var payload = ciphertext.StartsWith(Prefix, StringComparison.Ordinal)
            ? ciphertext[Prefix.Length..]
            : ciphertext;
        return _protector.Unprotect(payload);
    }

    public bool IsEncrypted(string? value)
        => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
}
