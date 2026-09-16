using SaasMultiTenant.Domain.Common;

namespace SaasMultiTenant.Domain.Users;

public enum UserRole
{
    /// <summary>Read only access within the tenant.</summary>
    Viewer,

    /// <summary>Can create and edit, but not manage users or the plan.</summary>
    Member,

    /// <summary>Full control over the tenant, including its users.</summary>
    Admin
}

public class User : TenantEntity
{
    private User()
    {
    }

    public User(string name, string email, string passwordHash, UserRole role = UserRole.Member)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("User name is required.");
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            throw new DomainException("A valid user email is required.");
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainException("A password hash is required.");
        }

        Name = name.Trim();
        Email = email.Trim().ToLowerInvariant();
        PasswordHash = passwordHash;
        Role = role;
        Active = true;
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Unique per tenant, not globally. The same person can hold an account at
    /// two companies using the platform, which is why sign in needs the tenant
    /// slug as well as the email.
    /// </summary>
    public string Email { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public UserRole Role { get; private set; }

    public bool Active { get; private set; }

    public DateTime? LastLoginAt { get; private set; }

    public void ChangeRole(UserRole role)
    {
        Role = role;
        Touch();
    }

    public void RecordLogin(DateTime at)
    {
        LastLoginAt = at;
        Touch();
    }

    public void Deactivate()
    {
        Active = false;
        Touch();
    }

    public void Reactivate()
    {
        Active = true;
        Touch();
    }
}
