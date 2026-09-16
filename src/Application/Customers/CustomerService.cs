using SaasMultiTenant.Application.Abstractions;
using SaasMultiTenant.Application.Common;
using SaasMultiTenant.Domain.Common;
using SaasMultiTenant.Domain.Customers;
using SaasMultiTenant.Domain.Tenants;

namespace SaasMultiTenant.Application.Customers;

public sealed record CreateCustomerRequest(string Name, string Email, string? Document, string? Phone);

public sealed record CustomerResponse(
    Guid Id,
    string Name,
    string Email,
    string? Document,
    string? Phone,
    bool Active,
    DateTime CreatedAt);

public sealed record PlanUsage(int Used, int Limit, bool Unlimited)
{
    public int? Remaining => Unlimited ? null : Math.Max(0, Limit - Used);
}

public interface ICustomerService
{
    Task<Result<CustomerResponse>> CreateAsync(
        CreateCustomerRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<CustomerResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PagedResponse<CustomerResponse>>> SearchAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PlanUsage>> UsageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Notice what is missing from every method here: a tenant id.
///
/// The repository queries are already scoped by the global query filter, and
/// new entities are stamped on save. A service that had to remember to filter
/// would eventually forget.
///
/// The one thing it does have to do explicitly is check the plan limit, because
/// that needs a count the domain cannot perform.
/// </summary>
public sealed class CustomerService : ICustomerService
{
    private readonly ICustomerRepository _customers;
    private readonly ITenantRepository _tenants;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    public CustomerService(
        ICustomerRepository customers,
        ITenantRepository tenants,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _customers = customers;
        _tenants = tenants;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    public async Task<Result<CustomerResponse>> CreateAsync(
        CreateCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenant = await CurrentTenantAsync(cancellationToken);

        if (tenant is null)
        {
            return Result.Failure<CustomerResponse>(Error.NotFound("The current tenant no longer exists."));
        }

        var currentCount = await _customers.CountAsync(cancellationToken);

        try
        {
            tenant.EnsureCanAdd(ResourceKind.Customer, currentCount);
        }
        catch (PlanLimitExceededException exception)
        {
            return Result.Failure<CustomerResponse>(Error.PlanLimit(exception.Message));
        }

        var customer = new Customer(request.Name, request.Email, request.Document, request.Phone);

        await _customers.AddAsync(customer, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(Map(customer));
    }

    public async Task<Result<CustomerResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await _customers.GetByIdAsync(id, cancellationToken);

        // A customer belonging to another tenant is filtered out before it gets
        // here, so this is a 404 rather than a 403. Telling the caller the row
        // exists but belongs to someone else would itself leak information.
        return customer is null
            ? Result.Failure<CustomerResponse>(Error.NotFound($"Customer {id} was not found."))
            : Result.Success(Map(customer));
    }

    public async Task<Result<PagedResponse<CustomerResponse>>> SearchAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (items, total) = await _customers.SearchAsync(search, page, pageSize, cancellationToken);

        return Result.Success(new PagedResponse<CustomerResponse>(
            items.Select(Map).ToList(),
            total,
            page,
            pageSize,
            (int)Math.Ceiling(total / (double)pageSize)));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await _customers.GetByIdAsync(id, cancellationToken);

        if (customer is null)
        {
            return Result.Failure(Error.NotFound($"Customer {id} was not found."));
        }

        customer.Deactivate();

        _customers.Update(customer);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result<PlanUsage>> UsageAsync(CancellationToken cancellationToken = default)
    {
        var tenant = await CurrentTenantAsync(cancellationToken);

        if (tenant is null)
        {
            return Result.Failure<PlanUsage>(Error.NotFound("The current tenant no longer exists."));
        }

        var used = await _customers.CountAsync(cancellationToken);

        return Result.Success(new PlanUsage(
            used,
            tenant.Plan.MaxCustomers,
            tenant.Plan.IsUnlimited(ResourceKind.Customer)));
    }

    private Task<Tenant?> CurrentTenantAsync(CancellationToken cancellationToken) =>
        _tenants.GetByIdAsync(_tenantContext.RequireTenantId(), cancellationToken);

    private static CustomerResponse Map(Customer customer) => new(
        customer.Id,
        customer.Name,
        customer.Email,
        customer.Document,
        customer.Phone,
        customer.Active,
        customer.CreatedAt);
}
