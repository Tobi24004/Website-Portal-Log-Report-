using System.Security.Cryptography;

namespace Portal.Api.Infrastructure;

/// <summary>
/// Băm mật khẩu người dùng một chiều bằng PBKDF2 (HMAC-SHA256).
/// Định dạng lưu: pbkdf2$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;   // 128-bit
    private const int KeySize = 32;    // 256-bit

    public static string Hash(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Mật khẩu không được rỗng.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations)) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>True nếu chuỗi hash đang là placeholder hoặc chưa đúng định dạng PBKDF2.</summary>
    public static bool IsPlaceholder(string? stored)
        => string.IsNullOrWhiteSpace(stored) || !stored.StartsWith("pbkdf2$", StringComparison.Ordinal);
}
