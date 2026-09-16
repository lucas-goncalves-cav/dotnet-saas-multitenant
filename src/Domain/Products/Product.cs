using SaasMultiTenant.Domain.Common;

namespace SaasMultiTenant.Domain.Products;

public class Product : TenantEntity
{
    private Product()
    {
    }

    public Product(string name, string? description, decimal price, int stock = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Product name is required.");
        }

        if (price <= 0)
        {
            throw new DomainException("Product price must be greater than zero.");
        }

        if (stock < 0)
        {
            throw new DomainException("Stock cannot be negative.");
        }

        Name = name.Trim();
        Description = description?.Trim();
        Price = price;
        Stock = stock;
        Active = true;
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public decimal Price { get; private set; }

    public int Stock { get; private set; }

    public bool Active { get; private set; }

    public void Update(string name, string? description, decimal price)
    {
        if (price <= 0)
        {
            throw new DomainException("Product price must be greater than zero.");
        }

        Name = name.Trim();
        Description = description?.Trim();
        Price = price;
        Touch();
    }

    public void RemoveStock(int quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException("Quantity must be greater than zero.");
        }

        if (quantity > Stock)
        {
            throw new DomainException($"Only {Stock} unit(s) of {Name} are available.");
        }

        Stock -= quantity;
        Touch();
    }

    public void AddStock(int quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException("Quantity must be greater than zero.");
        }

        Stock += quantity;
        Touch();
    }

    public void Deactivate()
    {
        Active = false;
        Touch();
    }
}
