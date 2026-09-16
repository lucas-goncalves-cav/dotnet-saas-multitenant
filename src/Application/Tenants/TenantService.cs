using SaasMultiTenant.Application.Abstractions;
using SaasMultiTenant.Application.Common;
using SaasMultiTenant.Application.Customers;
using SaasMultiTenant.Domain.Tenants;

namespace SaasMultiTenant.Application.Tenants;

public sealed record ChangePlanRequest(string Plan);

public sealed record PlanResponse(
    string Tier,
    int MaxUsers,
    int MaxCustomers,
    int MaxProducts,
    bool HasApiAccess);

public sealed record TenantResponse(
    Guid Id,
    string Name,
    string Slug,
    string PlanTier,
    bool Active,
    DateTime CreatedAt,
    PlanResponse Plan,
    PlanUsage Users,
    PlanUsage Customers,
    PlanUsage Products);

public interface ITenantService
{
    /// <summary>
    /// The tenant of the current request. There is no endpoint to fetch a
    /// tenant by id, because a caller has no business naming one.
    /// </summary>
    Task<Result<TenantResponse>> CurrentAsync(CancellationToken cancellationToken = default);

    Task<Result<TenantResponse>> ChangePlanAsync(
        ChangePlanRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<PlanResponse>>> PlansAsync(CancellationToken cancellationToken = default);
}

public sealed class TenantService : ITenantService
{
    private readonly ITenantRepository _tenants;
    private readonly IUserRepository _users;
    private readonly ICustomerRepository _customers;
    private readonly IProductRepository _products;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    public TenantService(
        ITenantRepository tenants,
        IUserRepository users,
        ICustomerRepository customers,
        IProductRepository products,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _tenants = tenants;
        _users = users;
        _customers = customers;
        _products = products;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    public async Task<Result<TenantResponse>> CurrentAsync(CancellationToken cancellationToken = default)
    {
        var tenant = await _tenants.GetByIdAsync(_tenantContext.RequireTenantId(), cancellationToken);

        return tenant is null
            ? Result.Failure<TenantResponse>(Error.NotFound("The current tenant no longer exists."))
            : Result.Success(await MapAsync(tenant, cancellationToken));
    }

    public async Task<Result<TenantResponse>> ChangePlanAsync(
        ChangePlanRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<PlanTier>(request.Plan, ignoreCase: true, out var tier))
        {
            return Result.Failure<TenantResponse>(
                Error.Validation($"Plan must be one of: {string.Join(", ", Enum.GetNames<PlanTier>())}."));
        }

        var tenant = await _tenants.GetByIdAsync(_tenantContext.RequireTenantId(), cancellationToken);

        if (tenant is null)
        {
            return Result.Failure<TenantResponse>(Error.NotFound("The current tenant no longer exists."));
        }

        var target = Plan.For(tier);

        // Downgrading below current usage is refused rather than silently
        // deleting the excess. Which customers to drop is the tenant's
        // decision, not the platform's.
        var overage = await FirstOverageAsync(target, cancellationToken);

        if (overage is not null)
        {
            return Result.Failure<TenantResponse>(Error.PlanLimit(overage));
        }

        tenant.ChangePlan(tier);

        _tenants.Update(tenant);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(await MapAsync(tenant, cancellationToken));
    }

    public Task<Result<IReadOnlyCollection<PlanResponse>>> PlansAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success<IReadOnlyCollection<PlanResponse>>(
            Plan.All.Select(MapPlan).ToList()));

    private async Task<string?> FirstOverageAsync(Plan target, CancellationToken cancellationToken)
    {
        var checks = new (ResourceKind Resource, int Count)[]
        {
            (ResourceKind.User, await _users.CountAsync(cancellationToken)),
            (ResourceKind.Customer, await _customers.CountAsync(cancellationToken)),
            (ResourceKind.Product, await _products.CountAsync(cancellationToken)),
        };

        foreach (var (resource, count) in checks)
        {
            var limit = target.LimitFor(resource);

            if (count > limit)
            {
                return $"This tenant has {count} {resource.ToString().ToLowerInvariant()}s, "
                       + $"and the {target.Tier} plan allows {limit}. "
                       + "Remove the excess before changing plan.";
            }
        }

        return null;
    }

    private async Task<TenantResponse> MapAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        var plan = tenant.Plan;

        return new TenantResponse(
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.PlanTier.ToString(),
            tenant.Active,
            tenant.CreatedAt,
            MapPlan(plan),
            Usage(await _users.CountAsync(cancellationToken), plan, ResourceKind.User),
            Usage(await _customers.CountAsync(cancellationToken), plan, ResourceKind.Customer),
            Usage(await _products.CountAsync(cancellationToken), plan, ResourceKind.Product));
    }

    private static PlanUsage Usage(int used, Plan plan, ResourceKind resource) =>
        new(used, plan.LimitFor(resource), plan.IsUnlimited(resource));

    private static PlanResponse MapPlan(Plan plan) => new(
        plan.Tier.ToString(),
        plan.MaxUsers,
        plan.MaxCustomers,
        plan.MaxProducts,
        plan.HasApiAccess);
}
