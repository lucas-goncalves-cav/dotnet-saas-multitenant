using SaasMultiTenant.Domain.Common;
using SaasMultiTenant.Domain.Customers;
using SaasMultiTenant.Domain.Products;

namespace SaasMultiTenant.Domain.Orders;

public enum OrderStatus
{
    Draft,
    Confirmed,
    Cancelled
}

public class OrderItem : TenantEntity
{
    private OrderItem()
    {
    }

    public OrderItem(Guid orderId, Product product, int quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException("Quantity must be greater than zero.");
        }

        OrderId = orderId;
        ProductId = product.Id;
        ProductName = product.Name;
        // The price is copied onto the line rather than read from the product
        // at display time, so a later price change does not rewrite history.
        UnitPrice = product.Price;
        Quantity = quantity;
    }

    public Guid OrderId { get; private set; }

    public Guid ProductId { get; private set; }

    public string ProductName { get; private set; } = string.Empty;

    public decimal UnitPrice { get; private set; }

    public int Quantity { get; private set; }

    public decimal LineTotal => UnitPrice * Quantity;
}

public class Order : TenantEntity
{
    private readonly List<OrderItem> _items = [];

    private Order()
    {
    }

    public Order(Customer customer, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new DomainException("An order reference is required.");
        }

        CustomerId = customer.Id;
        CustomerName = customer.Name;
        Reference = reference.Trim();
        Status = OrderStatus.Draft;
    }

    public Guid CustomerId { get; private set; }

    public string CustomerName { get; private set; } = string.Empty;

    public string Reference { get; private set; } = string.Empty;

    public OrderStatus Status { get; private set; }

    public DateTime? ConfirmedAt { get; private set; }

    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();

    public decimal Total => _items.Sum(item => item.LineTotal);

    public void AddItem(Product product, int quantity)
    {
        if (Status != OrderStatus.Draft)
        {
            throw new DomainException($"Items cannot be added to a {Status} order.");
        }

        // A product from another tenant should never reach this method, but
        // asserting it here means a bug in the service layer fails loudly
        // instead of silently creating a cross tenant order line.
        if (product.TenantId != TenantId && TenantId != Guid.Empty)
        {
            throw new DomainException("A product from a different tenant cannot be added to this order.");
        }

        product.RemoveStock(quantity);
        _items.Add(new OrderItem(Id, product, quantity));

        Touch();
    }

    public void Confirm()
    {
        if (Status != OrderStatus.Draft)
        {
            throw new DomainException($"A {Status} order cannot be confirmed.");
        }

        if (_items.Count == 0)
        {
            throw new DomainException("An order must contain at least one item before it is confirmed.");
        }

        Status = OrderStatus.Confirmed;
        ConfirmedAt = DateTime.UtcNow;
        Touch();
    }

    public void Cancel()
    {
        if (Status == OrderStatus.Cancelled)
        {
            return;
        }

        Status = OrderStatus.Cancelled;
        Touch();
    }
}
