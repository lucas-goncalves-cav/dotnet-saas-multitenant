using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SaasMultiTenant.Infrastructure.Auth;

namespace SaasMultiTenant.Infrastructure.Persistence;

/// <summary>
/// Used by the EF Core tooling, which has to build a context without a request.
///
/// The tenant context here has no tenant, which is correct: a migration is not
/// tenant scoped work, and the query filter plays no part in generating DDL.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Server=localhost,1433;Database=SaasMultiTenant;User Id=sa;Password=your_password_here;TrustServerCertificate=True;Encrypt=False";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new AppDbContext(options, new FixedTenantContext());
    }
}
