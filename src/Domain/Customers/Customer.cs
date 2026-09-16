using SaasMultiTenant.Domain.Common;

namespace SaasMultiTenant.Domain.Customers;

public class Customer : TenantEntity
{
    private Customer()
    {
    }

    public Customer(string name, string email, string? document = null, string? phone = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Customer name is required.");
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            throw new DomainException("A valid customer email is required.");
        }

        Name = name.Trim();
        Email = email.Trim().ToLowerInvariant();
        Document = document?.Trim();
        Phone = phone?.Trim();
        Active = true;
    }

    public string Name { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string? Document { get; private set; }

    public string? Phone { get; private set; }

    public bool Active { get; private set; }

    public void Update(string name, string email, string? document, string? phone)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Customer name is required.");
        }

        Name = name.Trim();
        Email = email.Trim().ToLowerInvariant();
        Document = document?.Trim();
        Phone = phone?.Trim();
        Touch();
    }

    public void Deactivate()
    {
        Active = false;
        Touch();
    }
}
