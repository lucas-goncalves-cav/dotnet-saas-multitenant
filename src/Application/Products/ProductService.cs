using SaasMultiTenant.Application.Abstractions;
using SaasMultiTenant.Application.Common;
using SaasMultiTenant.Application.Customers;
using SaasMultiTenant.Domain.Common;
using SaasMultiTenant.Domain.Products;
using SaasMultiTenant.Domain.Tenants;

namespace SaasMultiTenant.Application.Products;

public sealed record CreateProductRequest(string Name, string? Description, decimal Price, int Stock);

public sealed record UpdateProductRequest(string Name, string? Description, decimal Price);

public sealed record ProductResponse(
    Guid Id,
    string Name,
    string? Description,
    decimal Price,
    int Stock,
    bool Active,
    DateTime CreatedAt);

public interface IProductService
{
    Task<Result<ProductResponse>> CreateAsync(
        CreateProductRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ProductResponse>> UpdateAsync(
        Guid id,
        UpdateProductRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ProductResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PagedResponse<ProductResponse>>> SearchAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Result<ProductResponse>> AddStockAsync(
        Guid id,
        int quantity,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PlanUsage>> UsageAsync(CancellationToken cancellationToken = default);
}

public sealed class ProductService : IProductService
{
    private readonly IProductRepository _products;
    private readonly ITenantRepository _tenants;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    public ProductService(
        IProductRepository products,
        ITenantRepository tenants,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _products = products;
        _tenants = tenants;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    public async Task<Result<ProductResponse>> CreateAsync(
        CreateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenant = await CurrentTenantAsync(cancellationToken);

        if (tenant is null)
        {
            return Result.Failure<ProductResponse>(Error.NotFound("The current tenant no longer exists."));
        }

        // The count comes from a filtered query, so it counts this tenant's
        // products and nobody else's. A limit computed from an unfiltered count
        // would charge a small tenant for a large one's usage.
        var currentCount = await _products.CountAsync(cancellationToken);

        try
        {
            tenant.EnsureCanAdd(ResourceKind.Product, currentCount);
        }
        catch (PlanLimitExceededException exception)
        {
            return Result.Failure<ProductResponse>(Error.PlanLimit(exception.Message));
        }

        var product = new Product(request.Name, request.Description, request.Price, request.Stock);

        await _products.AddAsync(product, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(Map(product));
    }

    public async Task<Result<ProductResponse>> UpdateAsync(
        Guid id,
        UpdateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        var product = await _products.GetByIdAsync(id, cancellationToken);

        if (product is null)
        {
            return Result.Failure<ProductResponse>(Error.NotFound($"Product {id} was not found."));
        }

        product.Update(request.Name, request.Description, request.Price);

        _products.Update(product);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(Map(product));
    }

    public async Task<Result<ProductResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await _products.GetByIdAsync(id, cancellationToken);

        return product is null
            ? Result.Failure<ProductResponse>(Error.NotFound($"Product {id} was not found."))
            : Result.Success(Map(product));
    }

    public async Task<Result<PagedResponse<ProductResponse>>> SearchAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (items, total) = await _products.SearchAsync(search, page, pageSize, cancellationToken);

        return Result.Success(new PagedResponse<ProductResponse>(
            items.Select(Map).ToList(),
            total,
            page,
            pageSize,
            (int)Math.Ceiling(total / (double)pageSize)));
    }

    public async Task<Result<ProductResponse>> AddStockAsync(
        Guid id,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        var product = await _products.GetByIdAsync(id, cancellationToken);

        if (product is null)
        {
            return Result.Failure<ProductResponse>(Error.NotFound($"Product {id} was not found."));
        }

        try
        {
            product.AddStock(quantity);
        }
        catch (DomainException exception)
        {
            return Result.Failure<ProductResponse>(Error.Validation(exception.Message));
        }

        _products.Update(product);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(Map(product));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await _products.GetByIdAsync(id, cancellationToken);

        if (product is null)
        {
            return Result.Failure(Error.NotFound($"Product {id} was not found."));
        }

        product.Deactivate();

        _products.Update(product);
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

        var used = await _products.CountAsync(cancellationToken);

        return Result.Success(new PlanUsage(
            used,
            tenant.Plan.MaxProducts,
            tenant.Plan.IsUnlimited(ResourceKind.Product)));
    }

    private Task<Tenant?> CurrentTenantAsync(CancellationToken cancellationToken) =>
        _tenants.GetByIdAsync(_tenantContext.RequireTenantId(), cancellationToken);

    private static ProductResponse Map(Product product) => new(
        product.Id,
        product.Name,
        product.Description,
        product.Price,
        product.Stock,
        product.Active,
        product.CreatedAt);
}
