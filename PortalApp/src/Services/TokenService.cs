using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Portal.Api.Models;

namespace Portal.Api.Services;

public sealed class TokenService
{
    private readonly string _issuer;
    private readonly string _audience;
    private readonly SymmetricSecurityKey _key;
    private readonly int _expireMinutes;

    public TokenService(IConfiguration config)
    {
        _issuer = config["Portal:Jwt:Issuer"] ?? "PortalLogReport";
        _audience = config["Portal:Jwt:Audience"] ?? "PortalLogReport";
        var signing = config["Portal:Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Thiếu Portal:Jwt:SigningKey.");
        if (Encoding.UTF8.GetByteCount(signing) < 32)
            throw new InvalidOperationException("Portal:Jwt:SigningKey phải dài tối thiểu 32 ký tự.");
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signing));
        _expireMinutes = int.TryParse(config["Portal:Jwt:ExpireMinutes"], out var m) ? m : 60;
    }

    public (string Token, DateTime ExpiresAt) Create(UserInfo user)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(_expireMinutes);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        claims.AddRange(user.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(user.Permissions.Select(p => new Claim("perm", p)));
        if (user.MustChangePassword)
            claims.Add(new Claim("mustchange", "1"));

        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_issuer, _audience, claims, now, expires, creds);
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = _issuer,
        ValidAudience = _audience,
        IssuerSigningKey = _key,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
}
