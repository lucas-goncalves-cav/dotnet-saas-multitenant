using SaasMultiTenant.Application.Abstractions;
using SaasMultiTenant.Application.Common;
using SaasMultiTenant.Domain.Tenants;
using SaasMultiTenant.Domain.Users;

namespace SaasMultiTenant.Application.Auth;

/// <summary>
/// Sign in needs the tenant slug as well as the email, because an email is
/// only unique within a tenant. The same person can hold accounts at two
/// companies on the platform.
/// </summary>
public sealed record LoginRequest(string TenantSlug, string Email, string Password);

public sealed record LoginResponse(
    string AccessToken,
    DateTime ExpiresAt,
    Guid TenantId,
    string TenantName,
    string TenantSlug,
    string PlanTier,
    Guid UserId,
    string UserName,
    string Role);

public sealed record RegisterTenantRequest(
    string TenantName,
    string TenantSlug,
    string AdminName,
    string AdminEmail,
    string AdminPassword);

public interface IAuthService
{
    Task<Result<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<Result<LoginResponse>> RegisterTenantAsync(
        RegisterTenantRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITokenGenerator
{
    /// <summary>
    /// Issues a token carrying the tenant id as a claim.
    ///
    /// This is the only place a tenant id enters the system in a form the
    /// client will later present. Because the token is signed, the client can
    /// read the tenant id but cannot change it.
    /// </summary>
    (string Token, DateTime ExpiresAt) Generate(Tenant tenant, User user);
}

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);
}

/// <summary>
/// Reads tenants and users without the tenant filter, because sign in happens
/// before there is a tenant context. This is the one legitimate reason the
/// filter must be escapable.
/// </summary>
public interface IAuthRepository
{
    Task<Tenant?> GetTenantBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<bool> TenantSlugExistsAsync(string slug, CancellationToken cancellationToken = default);

    Task<User?> GetUserAsync(Guid tenantId, string email, CancellationToken cancellationToken = default);

    Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken = default);

    Task AddUserAsync(User user, Guid tenantId, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
