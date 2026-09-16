using SaasMultiTenant.Api.Extensions;
using SaasMultiTenant.Application.Customers;
using SaasMultiTenant.Infrastructure;

namespace SaasMultiTenant.Api.Endpoints;

/// <summary>
/// Look at what these handlers do not do.
///
/// None of them reads a tenant id, passes one down, or compares one. The route
/// has no tenant segment. Isolation is not the endpoint's job here, and an
/// endpoint that cannot express the wrong tenant cannot request it.
/// </summary>
public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customers")
            .WithTags("Customers")
            .RequireAuthorization(Policies.TenantMember);

        group.MapGet("/", async (
                string? search,
                int? page,
                int? pageSize,
                ICustomerService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.SearchAsync(
                    search,
                    Math.Max(1, page ?? 1),
                    Math.Clamp(pageSize ?? 20, 1, 100),
                    cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("SearchCustomers")
            .WithSummary("Lists the customers of the current tenant");

        group.MapGet("/{id:guid}", async (
                Guid id,
                ICustomerService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.GetByIdAsync(id, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("GetCustomer")
            .WithSummary("Fetches one customer")
            .WithDescription(
                "Another tenant's customer id returns 404, not 403. A 403 would confirm the row exists.");

        group.MapPost("/", async (
                CreateCustomerRequest request,
                ICustomerService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.CreateAsync(request, cancellationToken);

                return result.ToCreatedResult(customer => $"/api/customers/{customer.Id}");
            })
            .WithName("CreateCustomer")
            .WithSummary("Creates a customer")
            .WithDescription("Returns 402 when the plan limit for customers has been reached.");

        group.MapDelete("/{id:guid}", async (
                Guid id,
                ICustomerService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.DeleteAsync(id, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("DeleteCustomer")
            .WithSummary("Deactivates a customer");

        group.MapGet("/usage", async (ICustomerService service, CancellationToken cancellationToken) =>
            {
                var result = await service.UsageAsync(cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("CustomerUsage")
            .WithSummary("Reports customer usage against the plan limit");

        return app;
    }
}
