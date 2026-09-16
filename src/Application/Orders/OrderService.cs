using SaasMultiTenant.Application.Abstractions;
using SaasMultiTenant.Application.Common;
using SaasMultiTenant.Domain.Common;
using SaasMultiTenant.Domain.Orders;

namespace SaasMultiTenant.Application.Orders;

public sealed record OrderItemRequest(Guid ProductId, int Quantity);

public sealed record CreateOrderRequest(Guid CustomerId, string Reference, IReadOnlyCollection<OrderItemRequest> Items);

public sealed record OrderItemResponse(
    Guid Id,
    Guid ProductId,
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal);

public sealed record OrderResponse(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    string Reference,
    string Status,
    decimal Total,
    DateTime CreatedAt,
    DateTime? ConfirmedAt,
    IReadOnlyCollection<OrderItemResponse> Items);

public interface IOrderService
{
    Task<Result<OrderResponse>> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken = default);

    Task<Result<OrderResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PagedResponse<OrderResponse>>> SearchAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Result<OrderResponse>> ConfirmAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result> CancelAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// The most interesting service for tenant isolation, because an order pulls
/// together three entities.
///
/// A caller who sends another tenant's customer id or product id gets a plain
/// not found, and gets it without this class comparing a single tenant id. The
/// lookups run through the filter, so a foreign row is simply not there.
/// </summary>
public sealed class OrderService : IOrderService
{
    private readonly IOrderRepository _orders;
    private readonly ICustomerRepository _customers;
    private readonly IProductRepository _products;
    private readonly IUnitOfWork _unitOfWork;

    public OrderService(
        IOrderRepository orders,
        ICustomerRepository customers,
        IProductRepository products,
        IUnitOfWork unitOfWork)
    {
        _orders = orders;
        _customers = customers;
        _products = products;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<OrderResponse>> CreateAsync(
        CreateOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Items.Count == 0)
        {
            return Result.Failure<OrderResponse>(Error.Validation("An order must contain at least one item."));
        }

        var customer = await _customers.GetByIdAsync(request.CustomerId, cancellationToken);

        if (customer is null)
        {
            return Result.Failure<OrderResponse>(
                Error.NotFound($"Customer {request.CustomerId} was not found."));
        }

        if (await _orders.ReferenceExistsAsync(request.Reference, cancellationToken))
        {
            return Result.Failure<OrderResponse>(
                Error.Conflict($"An order with reference {request.Reference} already exists."));
        }

        Order order;

        try
        {
            order = new Order(customer, request.Reference);
        }
        catch (DomainException exception)
        {
            return Result.Failure<OrderResponse>(Error.Validation(exception.Message));
        }

        foreach (var item in request.Items)
        {
            var product = await _products.GetByIdAsync(item.ProductId, cancellationToken);

            // Another tenant's product id lands here as null, the same as an id
            // that never existed. That is the intended answer: confirming the
            // product exists elsewhere would leak the other tenant's catalogue.
            if (product is null)
            {
                return Result.Failure<OrderResponse>(Error.NotFound($"Product {item.ProductId} was not found."));
            }

            try
            {
                order.AddItem(product, item.Quantity);
            }
            catch (DomainException exception)
            {
                return Result.Failure<OrderResponse>(Error.Validation(exception.Message));
            }

            _products.Update(product);
        }

        await _orders.AddAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(Map(order));
    }

    public async Task<Result<OrderResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var order = await _orders.GetByIdAsync(id, cancellationToken);

        return order is null
            ? Result.Failure<OrderResponse>(Error.NotFound($"Order {id} was not found."))
            : Result.Success(Map(order));
    }

    public async Task<Result<PagedResponse<OrderResponse>>> SearchAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (items, total) = await _orders.SearchAsync(page, pageSize, cancellationToken);

        return Result.Success(new PagedResponse<OrderResponse>(
            items.Select(Map).ToList(),
            total,
            page,
            pageSize,
            (int)Math.Ceiling(total / (double)pageSize)));
    }

    public async Task<Result<OrderResponse>> ConfirmAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var order = await _orders.GetByIdAsync(id, cancellationToken);

        if (order is null)
        {
            return Result.Failure<OrderResponse>(Error.NotFound($"Order {id} was not found."));
        }

        try
        {
            order.Confirm();
        }
        catch (DomainException exception)
        {
            return Result.Failure<OrderResponse>(Error.Validation(exception.Message));
        }

        _orders.Update(order);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(Map(order));
    }

    public async Task<Result> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var order = await _orders.GetByIdAsync(id, cancellationToken);

        if (order is null)
        {
            return Result.Failure(Error.NotFound($"Order {id} was not found."));
        }

        order.Cancel();

        _orders.Update(order);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    private static OrderResponse Map(Order order) => new(
        order.Id,
        order.CustomerId,
        order.CustomerName,
        order.Reference,
        order.Status.ToString(),
        order.Total,
        order.CreatedAt,
        order.ConfirmedAt,
        order.Items
            .Select(item => new OrderItemResponse(
                item.Id,
                item.ProductId,
                item.ProductName,
                item.UnitPrice,
                item.Quantity,
                item.LineTotal))
            .ToList());
}
