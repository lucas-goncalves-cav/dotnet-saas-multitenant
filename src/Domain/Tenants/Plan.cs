namespace SaasMultiTenant.Domain.Tenants;

public enum PlanTier
{
    Free,
    Professional,
    Enterprise
}

/// <summary>
/// What a tenant is allowed to do on its plan.
///
/// Limits live in the domain rather than in configuration because they are
/// business rules: exceeding one is a domain error the API has to explain to
/// the customer, not an environment setting an operator tunes.
/// </summary>
public sealed record Plan
{
    private Plan(PlanTier tier, int maxUsers, int maxCustomers, int maxProductsPerTenant, bool hasApiAccess)
    {
        Tier = tier;
        MaxUsers = maxUsers;
        MaxCustomers = maxCustomers;
        MaxProducts = maxProductsPerTenant;
        HasApiAccess = hasApiAccess;
    }

    public PlanTier Tier { get; }

    public int MaxUsers { get; }

    public int MaxCustomers { get; }

    public int MaxProducts { get; }

    public bool HasApiAccess { get; }

    public static readonly Plan Free = new(PlanTier.Free, maxUsers: 5, maxCustomers: 100, maxProductsPerTenant: 50, hasApiAccess: false);

    public static readonly Plan Professional = new(PlanTier.Professional, maxUsers: 20, maxCustomers: 5_000, maxProductsPerTenant: 1_000, hasApiAccess: true);

    /// <summary>Unlimited, expressed as <see cref="int.MaxValue"/> so the
    /// comparison in <see cref="AllowsAnother"/> stays uniform.</summary>
    public static readonly Plan Enterprise = new(PlanTier.Enterprise, maxUsers: int.MaxValue, maxCustomers: int.MaxValue, maxProductsPerTenant: int.MaxValue, hasApiAccess: true);

    public static Plan For(PlanTier tier) => tier switch
    {
        PlanTier.Free => Free,
        PlanTier.Professional => Professional,
        PlanTier.Enterprise => Enterprise,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown plan tier.")
    };

    public static IReadOnlyCollection<Plan> All => [Free, Professional, Enterprise];

    public int LimitFor(ResourceKind resource) => resource switch
    {
        ResourceKind.User => MaxUsers,
        ResourceKind.Customer => MaxCustomers,
        ResourceKind.Product => MaxProducts,
        _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, "Unknown resource.")
    };

    /// <summary>Whether one more of this resource fits within the plan.</summary>
    public bool AllowsAnother(ResourceKind resource, int currentCount) => currentCount < LimitFor(resource);

    public bool IsUnlimited(ResourceKind resource) => LimitFor(resource) == int.MaxValue;
}

public enum ResourceKind
{
    User,
    Customer,
    Product
}
