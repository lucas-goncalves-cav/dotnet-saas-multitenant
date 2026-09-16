using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SaasMultiTenant.Application.Abstractions;

namespace SaasMultiTenant.Infrastructure.Auth;

/// <summary>
/// Resolves the tenant for the current request, from the validated token only.
///
/// What this class deliberately does not do is read a tenant id from a route
/// parameter, a query string, a header or the request body. Every one of those
/// is caller controlled, and a tenant id the caller controls is not an
/// isolation boundary.
///
/// The authentication middleware has already validated the signature and the
/// expiry by the time these claims are read, so a claim present here came from
/// a token this server issued.
/// </summary>
public sealed class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpTenantContext(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    private ClaimsPrincipal? User => _accessor.HttpContext?.User;

    public Guid? TenantId
    {
        get
        {
            var claim = User?.FindFirst(JwtTokenGenerator.TenantIdClaim)?.Value;

            return Guid.TryParse(claim, out var tenantId) ? tenantId : null;
        }
    }

    public Guid? UserId
    {
        get
        {
            // ASP.NET Core maps "sub" to NameIdentifier unless that mapping is
            // switched off, so both spellings are checked.
            var claim = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                        ?? User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            return Guid.TryParse(claim, out var userId) ? userId : null;
        }
    }

    public string? UserEmail =>
        User?.FindFirst(ClaimTypes.Email)?.Value
        ?? User?.FindFirst(JwtRegisteredClaimNames.Email)?.Value;

    public string? Role => User?.FindFirst(ClaimTypes.Role)?.Value;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true && TenantId is not null;

    public Guid RequireTenantId() => TenantId ?? throw new MissingTenantException();
}

/// <summary>
/// A fixed tenant context, for work that runs outside a request: migrations,
/// seeding and background jobs that act on a known tenant.
/// </summary>
public sealed class FixedTenantContext : ITenantContext
{
    public FixedTenantContext(Guid? tenantId = null, Guid? userId = null, string? role = null)
    {
        TenantId = tenantId;
        UserId = userId;
        Role = role;
    }

    public Guid? TenantId { get; }

    public Guid? UserId { get; }

    public string? UserEmail => null;

    public string? Role { get; }

    public bool IsAuthenticated => TenantId is not null;

    public Guid RequireTenantId() => TenantId ?? throw new MissingTenantException();
}
