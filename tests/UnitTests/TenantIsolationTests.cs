using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SaasMultiTenant.Application.Abstractions;
using SaasMultiTenant.Domain.Customers;
using SaasMultiTenant.Domain.Products;
using SaasMultiTenant.Domain.Tenants;
using SaasMultiTenant.Domain.Users;
using SaasMultiTenant.Infrastructure.Persistence;

namespace SaasMultiTenant.UnitTests;

/// <summary>
/// The tests that matter most in this repository.
///
/// Every one of them is an attempt to see or change another tenant's data.
/// A multi tenant system that has not been attacked by its own test suite has
/// not been shown to isolate anything.
/// </summary>
public class TenantIsolationTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _databaseName = $"isolation-{Guid.NewGuid()}";
    private readonly MutableTenantContext _context = new();

    /// <summary>
    /// A context bound to the shared database, acting as the given tenant.
    /// Each call is a separate DbContext, the way separate requests are.
    /// </summary>
    private AppDbContext ContextFor(Guid? tenantId)
    {
        _context.TenantId = tenantId;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;

        return new AppDbContext(options, _context);
    }

    private void SeedBothTenants()
    {
        using (var a = ContextFor(TenantA))
        {
            a.Customers.Add(new Customer("Customer of A", "a-customer@example.com"));
            a.Products.Add(new Product("Product of A", null, 100m, 10));
            a.Users.Add(new User("User A", "user@a.com", "hash", UserRole.Admin));
            a.SaveChanges();
        }

        using var b = ContextFor(TenantB);
        b.Customers.Add(new Customer("Customer of B", "b-customer@example.com"));
        b.Products.Add(new Product("Product of B", null, 200m, 20));
        b.Users.Add(new User("User B", "user@b.com", "hash", UserRole.Admin));
        b.SaveChanges();
    }

    // -------------------------------------------------------------------------
    // Reads
    // -------------------------------------------------------------------------

    [Fact]
    public void AQueryReturnsOnlyTheCurrentTenantsRows()
    {
        SeedBothTenants();

        using var context = ContextFor(TenantA);

        var customers = context.Customers.ToList();

        customers.Should().ContainSingle();
        customers[0].Name.Should().Be("Customer of A");
    }

    [Fact]
    public void EveryTenantOwnedEntityIsFiltered()
    {
        SeedBothTenants();

        using var context = ContextFor(TenantB);

        context.Customers.Should().ContainSingle();
        context.Products.Should().ContainSingle();
        context.Users.Should().ContainSingle();
    }

    /// <summary>
    /// The case a filter written by hand in each query would miss: fetching by
    /// primary key, where nobody thinks to add a tenant predicate.
    /// </summary>
    [Fact]
    public void FetchingAnotherTenantsRowByItsIdReturnsNothing()
    {
        SeedBothTenants();

        Guid foreignCustomerId;

        using (var b = ContextFor(TenantB))
        {
            foreignCustomerId = b.Customers.Single().Id;
        }

        using var a = ContextFor(TenantA);

        a.Customers.FirstOrDefault(customer => customer.Id == foreignCustomerId).Should().BeNull();
        a.Customers.Find(foreignCustomerId).Should().BeNull();
    }

    [Fact]
    public void CountsAndAggregatesAreAlsoScoped()
    {
        SeedBothTenants();

        using var context = ContextFor(TenantA);

        context.Products.Count().Should().Be(1);
        context.Products.Sum(product => product.Price).Should().Be(100m);
    }

    /// <summary>
    /// An unauthenticated request must see nothing, not everything. A filter
    /// that compares against null would be optimised away and return the whole
    /// table.
    /// </summary>
    [Fact]
    public void AnAnonymousContextSeesNothing()
    {
        SeedBothTenants();

        using var context = ContextFor(null);

        context.Customers.Should().BeEmpty();
        context.Products.Should().BeEmpty();
        context.Users.Should().BeEmpty();
    }

    [Fact]
    public void TheFilterFollowsTheTenantRatherThanBeingBakedIn()
    {
        SeedBothTenants();

        // The same model, two tenants, two results. If the filter captured the
        // tenant when the model was built, the second would be wrong.
        using (var a = ContextFor(TenantA))
        {
            a.Customers.Single().Name.Should().Be("Customer of A");
        }

        using var b = ContextFor(TenantB);
        b.Customers.Single().Name.Should().Be("Customer of B");
    }

    // -------------------------------------------------------------------------
    // Writes
    // -------------------------------------------------------------------------

    [Fact]
    public void ANewEntityIsStampedWithTheCurrentTenant()
    {
        using var context = ContextFor(TenantA);

        var customer = new Customer("Fresh", "fresh@example.com");
        customer.TenantId.Should().Be(Guid.Empty, "the domain does not set it");

        context.Customers.Add(customer);
        context.SaveChanges();

        customer.TenantId.Should().Be(TenantA);
    }

    [Fact]
    public void SavingWithoutATenantIsRejected()
    {
        using var context = ContextFor(null);

        context.Customers.Add(new Customer("Orphan", "orphan@example.com"));

        var act = () => context.SaveChanges();

        act.Should().Throw<MissingTenantException>();
    }

    /// <summary>
    /// The gap a query filter alone leaves open. An entity obtained outside the
    /// filter, here by attaching it, would otherwise be updated successfully.
    /// </summary>
    [Fact]
    public void UpdatingAnotherTenantsRowIsRejectedEvenWhenItIsAttachedDirectly()
    {
        SeedBothTenants();

        Customer foreign;

        using (var b = ContextFor(TenantB))
        {
            foreign = b.Customers.Single();
        }

        using var a = ContextFor(TenantA);

        a.Customers.Attach(foreign);
        foreign.Update("Renamed by A", "hacked@example.com", null, null);

        var act = () => a.SaveChanges();

        act.Should().Throw<CrossTenantAccessException>()
            .Which.OwnerTenantId.Should().Be(TenantB);
    }

    [Fact]
    public void DeletingAnotherTenantsRowIsRejected()
    {
        SeedBothTenants();

        Product foreign;

        using (var b = ContextFor(TenantB))
        {
            foreign = b.Products.Single();
        }

        using var a = ContextFor(TenantA);

        a.Products.Attach(foreign);
        a.Products.Remove(foreign);

        var act = () => a.SaveChanges();

        act.Should().Throw<CrossTenantAccessException>();
    }

    /// <summary>
    /// Rewriting TenantId is the most direct attack, and the check compares
    /// original values precisely so that it cannot be defeated this way.
    /// </summary>
    [Fact]
    public void RewritingTheTenantIdOnAnExistingRowIsRejected()
    {
        SeedBothTenants();

        using var context = ContextFor(TenantA);

        var customer = context.Customers.Single();
        var entry = context.Entry(customer);

        entry.CurrentValues[nameof(Domain.Common.TenantEntity.TenantId)] = TenantB;
        entry.State = EntityState.Modified;

        var act = () => context.SaveChanges();

        act.Should().Throw<CrossTenantAccessException>();
    }

    [Fact]
    public void ReassigningAnEntityToAnotherTenantIsRejectedByTheDomainToo()
    {
        var customer = new Customer("Owned", "owned@example.com");
        customer.AssignTenant(TenantA);

        var act = () => customer.AssignTenant(TenantB);

        act.Should().Throw<Domain.Common.DomainException>().WithMessage("*cannot be reassigned*");
    }

    [Fact]
    public void AssigningTheSameTenantTwiceIsHarmless()
    {
        var customer = new Customer("Owned", "owned@example.com");

        customer.AssignTenant(TenantA);
        customer.AssignTenant(TenantA);

        customer.TenantId.Should().Be(TenantA);
    }

    [Fact]
    public void UpdatingYourOwnRowWorks()
    {
        SeedBothTenants();

        using var context = ContextFor(TenantA);

        var customer = context.Customers.Single();
        customer.Update("Renamed by its owner", "renamed@example.com", null, null);

        context.SaveChanges();

        context.Customers.Single().Name.Should().Be("Renamed by its owner");
    }

    // -------------------------------------------------------------------------
    // The deliberate escape hatch
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sign in has to find a user before there is a tenant context, so the
    /// filter must be escapable. The method is named so every use is obvious
    /// in a diff.
    /// </summary>
    [Fact]
    public void TheFilterCanBeBypassedDeliberatelyForPlatformWork()
    {
        SeedBothTenants();

        using var context = ContextFor(TenantA);

        context.Customers.Should().ContainSingle();
        context.IgnoringTenantFilter<Customer>().Should().HaveCount(2);
    }

    [Fact]
    public void BypassingTheFilterDoesNotBypassTheWriteCheck()
    {
        SeedBothTenants();

        using var context = ContextFor(TenantA);

        var foreign = context.IgnoringTenantFilter<Customer>()
            .Single(customer => customer.TenantId == TenantB);

        foreign.Update("Renamed through the escape hatch", "x@example.com", null, null);

        var act = () => context.SaveChanges();

        act.Should().Throw<CrossTenantAccessException>();
    }

    public void Dispose()
    {
        using var context = ContextFor(TenantA);
        context.Database.EnsureDeleted();

        GC.SuppressFinalize(this);
    }

    private sealed class MutableTenantContext : ITenantContext
    {
        public Guid? TenantId { get; set; }

        public Guid? UserId { get; set; }

        public string? UserEmail { get; set; }

        public string? Role { get; set; }

        public bool IsAuthenticated => TenantId is not null;

        public Guid RequireTenantId() => TenantId ?? throw new MissingTenantException();
    }
}
