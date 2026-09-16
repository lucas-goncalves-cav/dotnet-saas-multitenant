using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SaasMultiTenant.Domain.Customers;
using SaasMultiTenant.Domain.Orders;
using SaasMultiTenant.Domain.Products;
using SaasMultiTenant.Domain.Tenants;
using SaasMultiTenant.Domain.Users;

namespace SaasMultiTenant.Infrastructure.Persistence;

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(tenant => tenant.Id);

        builder.Property(tenant => tenant.Name).IsRequired().HasMaxLength(200);
        builder.Property(tenant => tenant.Slug).IsRequired().HasMaxLength(40);
        builder.Property(tenant => tenant.PlanTier).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(tenant => tenant.SuspendedReason).HasMaxLength(500);

        builder.Ignore(tenant => tenant.Plan);

        builder.HasIndex(tenant => tenant.Slug).IsUnique();
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Name).IsRequired().HasMaxLength(200);
        builder.Property(user => user.Email).IsRequired().HasMaxLength(200);
        builder.Property(user => user.PasswordHash).IsRequired().HasMaxLength(500);
        builder.Property(user => user.Role).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Unique per tenant, not globally. The same person can hold an account
        // at two companies on the platform.
        builder.HasIndex(user => new { user.TenantId, user.Email }).IsUnique();

        ConfigureTenantColumn(builder);
    }

    /// <summary>
    /// Every tenant scoped query begins with TenantId, so every index on a
    /// tenant owned table leads with that column. What this method deliberately
    /// does not do is add a standalone index on TenantId: each configuration
    /// below already declares a composite index starting with it, and SQL
    /// Server can use a leading column prefix. A second index on the same
    /// leading column would be redundant and would cost a write on every
    /// insert for nothing.
    /// </summary>
    internal static void ConfigureTenantColumn<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class, Domain.Common.ITenantOwned
    {
        builder.Property(entity => entity.TenantId).IsRequired();
    }
}

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");
        builder.HasKey(customer => customer.Id);

        builder.Property(customer => customer.Name).IsRequired().HasMaxLength(200);
        builder.Property(customer => customer.Email).IsRequired().HasMaxLength(200);
        builder.Property(customer => customer.Document).HasMaxLength(20);
        builder.Property(customer => customer.Phone).HasMaxLength(30);

        builder.HasIndex(customer => new { customer.TenantId, customer.Email });

        UserConfiguration.ConfigureTenantColumn(builder);
    }
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        builder.HasKey(product => product.Id);

        builder.Property(product => product.Name).IsRequired().HasMaxLength(200);
        builder.Property(product => product.Description).HasMaxLength(1000);
        builder.Property(product => product.Price).HasPrecision(18, 2);

        builder.HasIndex(product => new { product.TenantId, product.Name });

        UserConfiguration.ConfigureTenantColumn(builder);
    }
}

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(order => order.Id);

        builder.Property(order => order.Reference).IsRequired().HasMaxLength(50);
        builder.Property(order => order.CustomerName).IsRequired().HasMaxLength(200);
        builder.Property(order => order.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Ignore(order => order.Total);

        builder.HasMany(order => order.Items)
            .WithOne()
            .HasForeignKey(item => item.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(order => order.Items)
            .HasField("_items")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(order => new { order.TenantId, order.Reference }).IsUnique();
        builder.HasIndex(order => new { order.TenantId, order.CustomerId });

        UserConfiguration.ConfigureTenantColumn(builder);
    }
}

public sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems");
        builder.HasKey(item => item.Id);

        builder.Property(item => item.ProductName).IsRequired().HasMaxLength(200);
        builder.Property(item => item.UnitPrice).HasPrecision(18, 2);

        builder.Ignore(item => item.LineTotal);

        // The one tenant owned table with no other tenant leading index, so
        // here the standalone one is not redundant.
        builder.HasIndex(item => new { item.TenantId, item.OrderId });

        UserConfiguration.ConfigureTenantColumn(builder);
    }
}
