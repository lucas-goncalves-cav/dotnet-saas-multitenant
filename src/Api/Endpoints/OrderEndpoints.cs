using SaasMultiTenant.Api.Extensions;
using SaasMultiTenant.Application.Orders;
using SaasMultiTenant.Infrastructure;

namespace SaasMultiTenant.Api.Endpoints;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/orders")
            .WithTags("Orders")
            .RequireAuthorization(Policies.TenantMember);

        group.MapGet("/", async (
                int? page,
                int? pageSize,
                IOrderService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.SearchAsync(
                    Math.Max(1, page ?? 1),
                    Math.Clamp(pageSize ?? 20, 1, 100),
                    cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("SearchOrders")
            .WithSummary("Lists the orders of the current tenant");

        group.MapGet("/{id:guid}", async (
                Guid id,
                IOrderService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.GetByIdAsync(id, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("GetOrder")
            .WithSummary("Fetches one order with its items");

        group.MapPost("/", async (
                CreateOrderRequest request,
                IOrderService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.CreateAsync(request, cancellationToken);

                return result.ToCreatedResult(order => $"/api/orders/{order.Id}");
            })
            .WithName("CreateOrder")
            .WithSummary("Creates an order")
            .WithDescription(
                "The customer id and every product id are resolved through the tenant filter, so ids "
                + "belonging to another tenant come back as 404.");

        group.MapPost("/{id:guid}/confirm", async (
                Guid id,
                IOrderService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.ConfirmAsync(id, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("ConfirmOrder")
            .WithSummary("Confirms a draft order");

        group.MapPost("/{id:guid}/cancel", async (
                Guid id,
                IOrderService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.CancelAsync(id, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("CancelOrder")
            .WithSummary("Cancels an order");

        return app;
    }
}
