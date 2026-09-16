using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SaasMultiTenant.Application.Abstractions;
using SaasMultiTenant.Domain.Common;
using SaasMultiTenant.Domain.Customers;
using SaasMultiTenant.Domain.Orders;
using SaasMultiTenant.Domain.Products;
using SaasMultiTenant.Domain.Tenants;
using SaasMultiTenant.Domain.Users;

namespace SaasMultiTenant.Infrastructure.Persistence;

/// <summary>
/// Where tenant isolation actually happens.
///
/// Two mechanisms, and both are needed:
///
///   Reads  A global query filter is applied to every tenant owned entity, so
///          a developer who forgets to filter by tenant still cannot see
///          another tenant's rows. Forgetting is the normal failure mode, and
///          a mechanism that depends on nobody forgetting is not a mechanism.
///
///   Writes SaveChanges stamps TenantId onto new entities and refuses to
///          persist a modification to a row belonging to someone else. A query
///          filter does not protect writes: an entity attached by id, or one
///          reached through a navigation, bypasses it.
/// </summary>
public class AppDbContext : DbContext, IUnitOfWork
{
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Set only by <see cref="OverrideTenant"/>, for the registration flow.
    /// </summary>
    private Guid? _tenantOverride;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            // Ids come from the domain. Left as store generated, EF classifies
            // a child appended to a tracked aggregate as an existing row and
            // issues an UPDATE for something never inserted.
            if (typeof(BaseEntity).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType)
                    .Property(nameof(BaseEntity.Id))
                    .ValueGeneratedNever();
            }

            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                ApplyTenantFilter(modelBuilder, entityType.ClrType);
            }
        }

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Adds <c>WHERE TenantId = @current</c> to every query against the type.
    ///
    /// The filter closes over <c>this</c> rather than over a captured value,
    /// so it reads the tenant at query time. Capturing the id when the model
    /// is built would bake one tenant into the compiled model and hand every
    /// request the first tenant the process ever saw.
    /// </summary>
    private void ApplyTenantFilter(ModelBuilder modelBuilder, Type entityType)
    {
        var parameter = Expression.Parameter(entityType, "entity");

        var tenantProperty = Expression.Property(parameter, nameof(ITenantOwned.TenantId));

        var currentTenant = Expression.Property(
            Expression.Constant(this),
            nameof(CurrentTenantId));

        var body = Expression.Equal(tenantProperty, currentTenant);

        modelBuilder.Entity(entityType).HasQueryFilter(Expression.Lambda(body, parameter));
    }

    /// <summary>
    /// Read by the query filter on every query.
    ///
    /// An anonymous request yields <see cref="Guid.Empty"/>, which matches no
    /// row. Returning null would make the comparison null and disable the
    /// filter, which is the opposite of what an unauthenticated request should
    /// get.
    /// </summary>
    public Guid CurrentTenantId => EffectiveTenantId ?? Guid.Empty;

    private Guid? EffectiveTenantId => _tenantOverride ?? _tenantContext.TenantId;

    /// <summary>
    /// Acts as the given tenant until the returned scope is disposed.
    ///
    /// There is exactly one legitimate use: registering a new tenant, where
    /// the request creates the tenant it is about to write the first user
    /// into, so there is no authenticated tenant yet. That is a genuine
    /// chicken and egg problem rather than an oversight.
    ///
    /// It is a method with a conspicuous name rather than a mutable property
    /// so that every use is visible in a diff and short lived by construction.
    /// </summary>
    public IDisposable OverrideTenant(Guid tenantId)
    {
        if (_tenantOverride is not null)
        {
            throw new InvalidOperationException("A tenant override is already active on this context.");
        }

        _tenantOverride = tenantId;

        return new TenantOverrideScope(this);
    }

    private sealed class TenantOverrideScope : IDisposable
    {
        private readonly AppDbContext _context;

        public TenantOverrideScope(AppDbContext context) => _context = context;

        public void Dispose() => _context._tenantOverride = null;
    }

    public override int SaveChanges()
    {
        EnforceTenantOnPendingChanges();

        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnforceTenantOnPendingChanges();

        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Stamps new entities and blocks writes that cross a tenant boundary.
    ///
    /// The delete case matters as much as the update one. A query filter stops
    /// a tenant from reading another's row, but <c>Remove</c> on an entity
    /// obtained some other way, through a navigation or an explicit Attach,
    /// would otherwise go straight through.
    /// </summary>
    private void EnforceTenantOnPendingChanges()
    {
        var tenantId = EffectiveTenantId;

        foreach (var entry in ChangeTracker.Entries<TenantEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (tenantId is null)
                    {
                        throw new MissingTenantException();
                    }

                    entry.Entity.AssignTenant(tenantId.Value);
                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    EnsureBelongsToCurrentTenant(entry, tenantId);
                    break;
            }
        }
    }

    private static void EnsureBelongsToCurrentTenant(EntityEntry<TenantEntity> entry, Guid? tenantId)
    {
        // The original value, not the current one. Comparing the current value
        // would let an attacker rewrite TenantId and then pass the check.
        var originalTenantId = entry.OriginalValues.GetValue<Guid>(nameof(TenantEntity.TenantId));

        if (tenantId is null || originalTenantId != tenantId.Value)
        {
            throw new CrossTenantAccessException(
                entry.Entity.GetType().Name,
                entry.Entity.Id,
                originalTenantId,
                tenantId);
        }

        // Reassigning TenantId on an update is always a bug or an attack.
        if (entry.State == EntityState.Modified)
        {
            var currentTenantId = entry.CurrentValues.GetValue<Guid>(nameof(TenantEntity.TenantId));

            if (currentTenantId != originalTenantId)
            {
                throw new CrossTenantAccessException(
                    entry.Entity.GetType().Name,
                    entry.Entity.Id,
                    originalTenantId,
                    currentTenantId);
            }
        }
    }

    /// <summary>
    /// Runs a query with the tenant filter disabled.
    ///
    /// Needed for genuinely cross tenant work: platform administration,
    /// sign in, which has to find the tenant before there is a tenant context,
    /// and reporting. Named so that it is obvious in a diff, because every use
    /// deserves a second look.
    /// </summary>
    public IQueryable<TEntity> IgnoringTenantFilter<TEntity>() where TEntity : class =>
        Set<TEntity>().IgnoreQueryFilters();
}

/// <summary>
/// A write was attempted against a row belonging to a different tenant.
///
/// Reaching this is either a bug in the application or an attack. Either way
/// it must fail loudly, never silently succeed against the wrong rows.
/// </summary>
public sealed class CrossTenantAccessException : Exception
{
    public CrossTenantAccessException(string entityName, Guid entityId, Guid ownerTenantId, Guid? actingTenantId)
        : base(
            $"Tenant {actingTenantId?.ToString() ?? "(none)"} attempted to modify {entityName} {entityId}, "
            + $"which belongs to tenant {ownerTenantId}.")
    {
        EntityName = entityName;
        EntityId = entityId;
        OwnerTenantId = ownerTenantId;
        ActingTenantId = actingTenantId;
    }

    public string EntityName { get; }

    public Guid EntityId { get; }

    public Guid OwnerTenantId { get; }

    public Guid? ActingTenantId { get; }
}
