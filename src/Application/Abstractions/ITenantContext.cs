namespace SaasMultiTenant.Application.Abstractions;

/// <summary>
/// Who is making the current request, and which tenant they belong to.
///
/// This is the single source of truth for tenant identity. It is populated
/// from the validated JWT and from nowhere else: not from a route parameter,
/// not from a header, not from the request body.
///
/// That rule is the whole security model. A tenant id the client can influence
/// is a tenant id the client can change.
/// </summary>
public interface ITenantContext
{
    /// <summary>The authenticated tenant, or null on an anonymous request.</summary>
    Guid? TenantId { get; }

    Guid? UserId { get; }

    string? UserEmail { get; }

    string? Role { get; }

    bool IsAuthenticated { get; }

    /// <summary>
    /// The tenant id, or an exception. Used by code that cannot meaningfully
    /// proceed without one, so a missing tenant fails immediately rather than
    /// falling through to a query that would return another tenant's rows.
    /// </summary>
    Guid RequireTenantId();
}

/// <summary>
/// Raised when tenant scoped work is attempted without an authenticated
/// tenant. Treated as a server error rather than a 401, because reaching it
/// means authorization was misconfigured: the endpoint should have rejected
/// the request before any data access.
/// </summary>
public sealed class MissingTenantException : InvalidOperationException
{
    public MissingTenantException()
        : base("This operation requires an authenticated tenant, but none was present on the request.")
    {
    }
}
