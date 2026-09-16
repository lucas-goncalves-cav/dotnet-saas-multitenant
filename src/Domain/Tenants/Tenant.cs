using System.Text.RegularExpressions;
using SaasMultiTenant.Domain.Common;

namespace SaasMultiTenant.Domain.Tenants;

/// <summary>
/// One company using the platform.
///
/// Note that <see cref="Tenant"/> itself is not a <see cref="TenantEntity"/>:
/// it is the thing every other entity belongs to, so it sits outside the
/// filter and is queried by id or slug directly.
/// </summary>
public partial class Tenant : BaseEntity
{
    private Tenant()
    {
    }

    public Tenant(string name, string slug, PlanTier plan = PlanTier.Free)
    {
        SetName(name);
        SetSlug(slug);

        PlanTier = plan;
        Active = true;
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// URL safe identifier, used for sign in and for tenant resolution from a
    /// subdomain. Unique across the platform.
    /// </summary>
    public string Slug { get; private set; } = string.Empty;

    public PlanTier PlanTier { get; private set; }

    public bool Active { get; private set; }

    public DateTime? SuspendedAt { get; private set; }

    public string? SuspendedReason { get; private set; }

    public Plan Plan => Plan.For(PlanTier);

    public void ChangePlan(PlanTier plan)
    {
        PlanTier = plan;
        Touch();
    }

    public void Suspend(string reason)
    {
        Active = false;
        SuspendedAt = DateTime.UtcNow;
        SuspendedReason = reason;
        Touch();
    }

    public void Reactivate()
    {
        Active = true;
        SuspendedAt = null;
        SuspendedReason = null;
        Touch();
    }

    public void Rename(string name)
    {
        SetName(name);
        Touch();
    }

    /// <summary>
    /// Checks a plan limit before a resource is created.
    ///
    /// The count is passed in rather than read here, because the domain has no
    /// database access and the count is a query the caller already has to make.
    /// </summary>
    public void EnsureCanAdd(ResourceKind resource, int currentCount)
    {
        if (Plan.AllowsAnother(resource, currentCount))
        {
            return;
        }

        throw new PlanLimitExceededException(
            resource.ToString().ToLowerInvariant() + "s",
            Plan.LimitFor(resource),
            PlanTier.ToString());
    }

    private void SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Tenant name is required.");
        }

        Name = name.Trim();
    }

    private void SetSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new DomainException("Tenant slug is required.");
        }

        var normalized = slug.Trim().ToLowerInvariant();

        if (!SlugPattern().IsMatch(normalized))
        {
            throw new DomainException(
                "Tenant slug must be 3 to 40 characters of lowercase letters, digits and hyphens, "
                + "and cannot start or end with a hyphen.");
        }

        Slug = normalized;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,38}[a-z0-9])$")]
    private static partial Regex SlugPattern();
}
