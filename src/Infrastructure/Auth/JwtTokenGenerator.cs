using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using SaasMultiTenant.Application.Auth;
using SaasMultiTenant.Domain.Tenants;
using SaasMultiTenant.Domain.Users;

namespace SaasMultiTenant.Infrastructure.Auth;

public sealed record JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; init; } = "saas-multitenant";

    public string Audience { get; init; } = "saas-multitenant";

    /// <summary>
    /// Must be at least 32 bytes for HS256. Validated at startup rather than
    /// on first use, so a misconfigured deployment fails immediately instead
    /// of at the first sign in attempt.
    /// </summary>
    public string SigningKey { get; init; } = string.Empty;

    public int ExpiryMinutes { get; init; } = 60;
}

/// <summary>
/// Issues the token that carries the tenant id.
///
/// The claims below are the entire basis for tenant isolation at runtime. They
/// are signed, so a client can decode and read them but cannot alter them
/// without invalidating the signature.
/// </summary>
public sealed class JwtTokenGenerator : ITokenGenerator
{
    /// <summary>The claim every tenant scoped request is resolved from.</summary>
    public const string TenantIdClaim = "tenant_id";

    public const string TenantSlugClaim = "tenant_slug";

    public const string PlanClaim = "plan";

    private readonly JwtOptions _options;

    public JwtTokenGenerator(JwtOptions options)
    {
        if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
        {
            throw new InvalidOperationException(
                "The JWT signing key must be at least 32 bytes. Set Jwt:SigningKey to a long random value.");
        }

        _options = options;
    }

    public (string Token, DateTime ExpiresAt) Generate(Tenant tenant, User user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Role, user.Role.ToString()),

            // The one that matters. Every query the request makes is scoped by
            // this value, read from the validated token and nowhere else.
            new(TenantIdClaim, tenant.Id.ToString()),

            // Convenience for the client. The server never trusts these for
            // authorization; the tenant id claim is the only one that decides
            // anything.
            new(TenantSlugClaim, tenant.Slug),
            new(PlanClaim, tenant.PlanTier.ToString()),
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

/// <summary>
/// PBKDF2 with a per password salt.
///
/// Not the strongest option available: Argon2id is better and is what a new
/// production system should use. PBKDF2 is chosen here because it is in the
/// base class library, so the example has no extra dependency, and because
/// the parameters are visible and explainable.
/// </summary>
public sealed class Pbkdf2PasswordHasher : Application.Auth.IPasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 210_000;

    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, KeySize);

        // The parameters travel with the hash, so they can be raised later
        // without invalidating existing passwords.
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string hash)
    {
        var parts = hash.Split('.', 3);

        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expected.Length);

            // Fixed time comparison, so the number of matching leading bytes
            // cannot be inferred from how long the check takes.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
