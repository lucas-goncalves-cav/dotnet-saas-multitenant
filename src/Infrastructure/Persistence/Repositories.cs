using Microsoft.EntityFrameworkCore;
using SaasMultiTenant.Application.Abstractions;
using SaasMultiTenant.Application.Auth;
using SaasMultiTenant.Domain.Customers;
using SaasMultiTenant.Domain.Orders;
using SaasMultiTenant.Domain.Products;
using SaasMultiTenant.Domain.Tenants;
using SaasMultiTenant.Domain.Users;

namespace SaasMultiTenant.Infrastructure.Persistence;

/// <summary>
/// Notice that no query below mentions TenantId.
///
/// The global query filter has already added it. Repeating the predicate here
/// would be harmless but misleading, because it would suggest the filtering
/// depends on remembering to write it.
/// </summary>
public sealed class CustomerRepository : ICustomerRepository
{
    private readonly AppDbContext _context;

    public CustomerRepository(AppDbContext context)
    {
        _context = context;
    }

    public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Customers.FirstOrDefaultAsync(customer => customer.Id == id, cancellationToken);

    public async Task<(IReadOnlyCollection<Customer> Items, int Total)> SearchAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Customers.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(customer =>
                customer.Name.Contains(search) || customer.Email.Contains(search));
        }

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(customer => customer.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _context.Customers.CountAsync(cancellationToken);

    public async Task AddAsync(Customer customer, CancellationToken cancellationToken = default) =>
        await _context.Customers.AddAsync(customer, cancellationToken);

    public void Update(Customer customer) => MarkModified(_context, customer);

    /// <summary>
    /// Entities from this repository are already tracked, so changes are
    /// detected automatically. Calling Update on a tracked entity marks the
    /// whole graph as Modified, which makes EF issue an UPDATE for children
    /// that were only just added.
    /// </summary>
    internal static void MarkModified<TEntity>(AppDbContext context, TEntity entity) where TEntity : class
    {
        if (context.Entry(entity).State == EntityState.Detached)
        {
            context.Update(entity);
        }
    }
}

public sealed class ProductRepository : IProductRepository
{
    private readonly AppDbContext _context;

    public ProductRepository(AppDbContext context)
    {
        _context = context;
    }

    public Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Products.FirstOrDefaultAsync(product => product.Id == id, cancellationToken);

    public async Task<(IReadOnlyCollection<Product> Items, int Total)> SearchAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Products.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(product => product.Name.Contains(search));
        }

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(product => product.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _context.Products.CountAsync(cancellationToken);

    public async Task AddAsync(Product product, CancellationToken cancellationToken = default) =>
        await _context.Products.AddAsync(product, cancellationToken);

    public void Update(Product product) => CustomerRepository.MarkModified(_context, product);
}

public sealed class UserRepository : IUserRepository
{
    private readonly AppDbContext _context;

    public UserRepository(AppDbContext context)
    {
        _context = context;
    }

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Users.FirstOrDefaultAsync(user => user.Id == id, cancellationToken);

    public async Task<IReadOnlyCollection<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _context.Users.AsNoTracking().OrderBy(user => user.Name).ToListAsync(cancellationToken);

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) =>
        _context.Users.AnyAsync(user => user.Email == email.ToLower(), cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _context.Users.CountAsync(cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken = default) =>
        await _context.Users.AddAsync(user, cancellationToken);

    public void Update(User user) => CustomerRepository.MarkModified(_context, user);
}

public sealed class OrderRepository : IOrderRepository
{
    private readonly AppDbContext _context;

    public OrderRepository(AppDbContext context)
    {
        _context = context;
    }

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Orders
            .Include(order => order.Items)
            .FirstOrDefaultAsync(order => order.Id == id, cancellationToken);

    public async Task<(IReadOnlyCollection<Order> Items, int Total)> SearchAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Orders.Include(order => order.Items).AsNoTracking();

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(order => order.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<bool> ReferenceExistsAsync(string reference, CancellationToken cancellationToken = default) =>
        _context.Orders.AnyAsync(order => order.Reference == reference, cancellationToken);

    public async Task AddAsync(Order order, CancellationToken cancellationToken = default) =>
        await _context.Orders.AddAsync(order, cancellationToken);

    public void Update(Order order) => CustomerRepository.MarkModified(_context, order);
}

/// <summary>
/// Tenants are not tenant scoped, so this repository queries them directly.
/// </summary>
public sealed class TenantRepository : ITenantRepository
{
    private readonly AppDbContext _context;

    public TenantRepository(AppDbContext context)
    {
        _context = context;
    }

    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Tenants.FirstOrDefaultAsync(tenant => tenant.Id == id, cancellationToken);

    public Task<Tenant?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        _context.Tenants.FirstOrDefaultAsync(tenant => tenant.Slug == slug, cancellationToken);

    public void Update(Tenant tenant) => CustomerRepository.MarkModified(_context, tenant);
}

/// <summary>
/// The one repository that deliberately bypasses the tenant filter.
///
/// Sign in has to find a user before there is a tenant context, which is a
/// genuine chicken and egg problem rather than an oversight. Keeping it in its
/// own class means the bypass appears in exactly one file.
/// </summary>
public sealed class AuthRepository : IAuthRepository
{
    private readonly AppDbContext _context;

    public AuthRepository(AppDbContext context)
    {
        _context = context;
    }

    public Task<Tenant?> GetTenantBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        _context.Tenants.FirstOrDefaultAsync(tenant => tenant.Slug == slug.ToLower(), cancellationToken);

    public Task<bool> TenantSlugExistsAsync(string slug, CancellationToken cancellationToken = default) =>
        _context.Tenants.AnyAsync(tenant => tenant.Slug == slug.ToLower(), cancellationToken);

    /// <summary>
    /// Looks up a user without the tenant filter, but still scoped to the
    /// tenant explicitly. The bypass is about when the filter can run, not
    /// about ignoring which tenant the user belongs to.
    /// </summary>
    public Task<User?> GetUserAsync(Guid tenantId, string email, CancellationToken cancellationToken = default) =>
        _context.IgnoringTenantFilter<User>()
            .FirstOrDefaultAsync(
                user => user.TenantId == tenantId && user.Email == email.ToLower(),
                cancellationToken);

    public async Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken = default) =>
        await _context.Tenants.AddAsync(tenant, cancellationToken);

    public async Task AddUserAsync(User user, Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Registration creates the tenant it is writing into, so there is no
        // authenticated tenant yet and SaveChanges would refuse to stamp this
        // entity. The override says, explicitly and briefly, which tenant this
        // work belongs to.
        _registrationTenantId = tenantId;

        await _context.Users.AddAsync(user, cancellationToken);
    }

    private Guid? _registrationTenantId;

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (_registrationTenantId is null)
        {
            await _context.SaveChangesAsync(cancellationToken);

            return;
        }

        using var scope = _context.OverrideTenant(_registrationTenantId.Value);

        await _context.SaveChangesAsync(cancellationToken);

        _registrationTenantId = null;
    }
}
