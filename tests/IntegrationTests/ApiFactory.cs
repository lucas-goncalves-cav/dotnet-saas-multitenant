using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SaasMultiTenant.Application.Auth;
using SaasMultiTenant.Infrastructure.Persistence;

namespace SaasMultiTenant.IntegrationTests;

/// <summary>
/// Hosts the real application: real authentication, real middleware, real
/// DbContext with its query filters and its write guard.
///
/// Only the database provider is swapped, for an isolated in memory one per
/// test class. The query filters and the SaveChanges guard are EF Core
/// mechanisms rather than SQL Server ones, so they behave the same here.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"saas-tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // A placeholder, present only so the application can start.
                // The provider is replaced below, so nothing connects to it.
                ["ConnectionStrings:Default"] = "Server=(local);Database=ignored;Trusted_Connection=True",
                ["Jwt:SigningKey"] = "integration_tests_signing_key_that_is_long_enough_32",
                ["Jwt:Issuer"] = "saas-multitenant-tests",
                ["Jwt:Audience"] = "saas-multitenant-tests",
                ["Database:MigrateOnStartup"] = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Removing the registration is not enough on its own: the options
            // configuration added by AddDbContext survives, and EF then reports
            // two providers registered for the same context.
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();

            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }

    /// <summary>
    /// Registers a tenant through the public API and returns a client already
    /// carrying its token, which is how a real caller would obtain one.
    /// </summary>
    public async Task<TenantClient> RegisterTenantAsync(string slug, string name)
    {
        var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterTenantRequest(
            name,
            slug,
            $"Admin {name}",
            $"admin@{slug}.test",
            "correct horse battery staple"));

        response.EnsureSuccessStatusCode();

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>()
            ?? throw new InvalidOperationException("Registration returned no body.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        return new TenantClient(client, login);
    }

    /// <summary>
    /// A DbContext acting as the given tenant, for arranging state directly or
    /// for asserting what actually reached the database.
    /// </summary>
    public AsTenant Scope(Guid tenantId) => new(Services, tenantId);
}

public sealed record TenantClient(HttpClient Client, LoginResponse Login)
{
    public Guid TenantId => Login.TenantId;

    public Guid UserId => Login.UserId;
}

public sealed class AsTenant : IDisposable
{
    private readonly IServiceScope _scope;

    public AsTenant(IServiceProvider services, Guid tenantId)
    {
        _scope = services.CreateScope();

        var options = _scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        Context = new AppDbContext(options, new SaasMultiTenant.Infrastructure.Auth.FixedTenantContext(tenantId));
    }

    public AppDbContext Context { get; }

    public void Dispose()
    {
        Context.Dispose();
        _scope.Dispose();
    }
}
