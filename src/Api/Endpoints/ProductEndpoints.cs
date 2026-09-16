using SaasMultiTenant.Api.Extensions;
using SaasMultiTenant.Application.Products;
using SaasMultiTenant.Infrastructure;

namespace SaasMultiTenant.Api.Endpoints;

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/products")
            .WithTags("Products")
            .RequireAuthorization(Policies.TenantMember);

        group.MapGet("/", async (
                string? search,
                int? page,
                int? pageSize,
                IProductService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.SearchAsync(
                    search,
                    Math.Max(1, page ?? 1),
                    Math.Clamp(pageSize ?? 20, 1, 100),
                    cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("SearchProducts")
            .WithSummary("Lists the products of the current tenant");

        group.MapGet("/{id:guid}", async (
                Guid id,
                IProductService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.GetByIdAsync(id, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("GetProduct")
            .WithSummary("Fetches one product");

        group.MapPost("/", async (
                CreateProductRequest request,
                IProductService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.CreateAsync(request, cancellationToken);

                return result.ToCreatedResult(product => $"/api/products/{product.Id}");
            })
            .WithName("CreateProduct")
            .WithSummary("Creates a product");

        group.MapPut("/{id:guid}", async (
                Guid id,
                UpdateProductRequest request,
                IProductService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.UpdateAsync(id, request, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("UpdateProduct")
            .WithSummary("Updates a product");

        group.MapPost("/{id:guid}/stock", async (
                Guid id,
                AddStockRequest request,
                IProductService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.AddStockAsync(id, request.Quantity, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("AddProductStock")
            .WithSummary("Adds stock to a product");

        group.MapDelete("/{id:guid}", async (
                Guid id,
                IProductService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.DeleteAsync(id, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("DeleteProduct")
            .WithSummary("Deactivates a product");

        group.MapGet("/usage", async (IProductService service, CancellationToken cancellationToken) =>
            {
                var result = await service.UsageAsync(cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("ProductUsage")
            .WithSummary("Reports product usage against the plan limit");

        return app;
    }
}

public sealed record AddStockRequest(int Quantity);
