using SaasMultiTenant.Api.Extensions;
using SaasMultiTenant.Application.Auth;

namespace SaasMultiTenant.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth")
            .WithTags("Auth")
            // The only anonymous group in the application. Everything else
            // inherits the fallback policy, which requires a tenant claim.
            .AllowAnonymous();

        group.MapPost("/register", async (
                RegisterTenantRequest request,
                IAuthService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.RegisterTenantAsync(request, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("RegisterTenant")
            .WithSummary("Creates a tenant and its first administrator")
            .WithDescription(
                "Sign up for the platform. Returns a token for the new tenant, so the caller can continue "
                + "without a second round trip.");

        group.MapPost("/login", async (
                LoginRequest request,
                IAuthService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.LoginAsync(request, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("Login")
            .WithSummary("Signs in to a tenant")
            .WithDescription(
                "The tenant slug is part of the request because an email is unique per tenant, not across "
                + "the platform. The returned token carries the tenant id, and every later request is scoped "
                + "by that claim.");

        return app;
    }
}
