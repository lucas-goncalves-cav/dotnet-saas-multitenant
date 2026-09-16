using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SaasMultiTenant.Application.Abstractions;
using SaasMultiTenant.Application.Auth;
using SaasMultiTenant.Application.Customers;
using SaasMultiTenant.Application.Orders;
using SaasMultiTenant.Application.Products;
using SaasMultiTenant.Application.Tenants;
using SaasMultiTenant.Application.Users;
using SaasMultiTenant.Infrastructure.Auth;
using SaasMultiTenant.Infrastructure.Persistence;

namespace SaasMultiTenant.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is not set. See .env.example for the expected format.");

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));

        // Scoped, because the tenant is a property of the request. A singleton
        // here would serve the first caller's tenant to everyone after them,
        // which is the single worst bug this kind of system can have.
        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContext, HttpTenantContext>();

        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<AppDbContext>());

        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IAuthRepository, AuthRepository>();

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<ITenantService, TenantService>();

        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

        return services;
    }

    /// <summary>
    /// Wires up bearer authentication.
    ///
    /// The validation parameters below are what make the tenant claim
    /// trustworthy. Turning off any of them, in particular the signature check,
    /// would let a caller mint a token naming any tenant they like, and every
    /// other isolation mechanism in this project reads the tenant from that
    /// token.
    /// </summary>
    public static IServiceCollection AddTenantAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("The Jwt configuration section is missing.");

        // Constructed here rather than resolved lazily, so a signing key that
        // is missing or too short stops the application at startup instead of
        // at the first sign in.
        var tokenGenerator = new JwtTokenGenerator(options);

        services.AddSingleton(options);
        services.AddSingleton<ITokenGenerator>(tokenGenerator);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(bearer =>
            {
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = options.Issuer,
                    ValidAudience = options.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),

                    // The default allows five minutes of drift, which means a
                    // token stays usable for five minutes after it expires.
                    ClockSkew = TimeSpan.Zero,
                };
            });

        services.AddAuthorization(authorization =>
        {
            // Every policy requires the tenant claim. An authenticated token
            // without one cannot reach a tenant scoped endpoint at all, rather
            // than reaching it and failing deeper in with an obscure error.
            authorization.AddPolicy(Policies.TenantMember, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(JwtTokenGenerator.TenantIdClaim));

            authorization.AddPolicy(Policies.TenantAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(JwtTokenGenerator.TenantIdClaim)
                .RequireRole("Admin"));

            authorization.FallbackPolicy = authorization.GetPolicy(Policies.TenantMember);
        });

        return services;
    }
}

public static class Policies
{
    /// <summary>Any signed in user of a tenant.</summary>
    public const string TenantMember = "TenantMember";

    /// <summary>Administrators of the tenant, for user and plan management.</summary>
    public const string TenantAdmin = "TenantAdmin";
}
