namespace SaasMultiTenant.Domain.Common;

public abstract class BaseEntity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; protected set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; protected set; }

    protected void Touch() => UpdatedAt = DateTime.UtcNow;
}

/// <summary>
/// Marks an entity as belonging to exactly one tenant.
///
/// Every entity carrying this interface gets a global query filter applied
/// automatically, which is what makes isolation a property of the data access
/// layer rather than something each query has to remember.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

/// <summary>
/// A tenant owned entity. The setter is deliberately absent: TenantId is
/// assigned once, by the DbContext, from the authenticated request.
/// Letting application code set it is how cross tenant writes happen.
/// </summary>
public abstract class TenantEntity : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }

    /// <summary>
    /// Called by the persistence layer when the entity is first saved.
    /// Throws if something tries to move an entity between tenants.
    /// </summary>
    public void AssignTenant(Guid tenantId)
    {
        if (TenantId != Guid.Empty && TenantId != tenantId)
        {
            throw new DomainException(
                $"Entity {Id} belongs to tenant {TenantId} and cannot be reassigned to {tenantId}.");
        }

        TenantId = tenantId;
    }
}
