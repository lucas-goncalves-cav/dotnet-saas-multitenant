using SaasMultiTenant.Api.Extensions;
using SaasMultiTenant.Application.Tenants;
using SaasMultiTenant.Application.Users;
using SaasMultiTenant.Infrastructure;

namespace SaasMultiTenant.Api.Endpoints;

public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tenant")
            .WithTags("Tenant")
            .RequireAuthorization(Policies.TenantMember);

        // There is no GET /api/tenants/{id}. The current tenant comes from the
        // token, so there is no route a caller could point at someone else.
        group.MapGet("/", async (ITenantService service, CancellationToken cancellationToken) =>
            {
                var result = await service.CurrentAsync(cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("GetCurrentTenant")
            .WithSummary("Returns the current tenant with its plan and usage");

        group.MapPut("/plan", async (
                ChangePlanRequest request,
                ITenantService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.ChangePlanAsync(request, cancellationToken);

                return result.ToHttpResult();
            })
            .RequireAuthorization(Policies.TenantAdmin)
            .WithName("ChangePlan")
            .WithSummary("Changes the plan of the current tenant")
            .WithDescription("A downgrade below current usage is refused, rather than deleting the excess.");

        group.MapGet("/plans", async (ITenantService service, CancellationToken cancellationToken) =>
            {
                var result = await service.PlansAsync(cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("ListPlans")
            .WithSummary("Lists the available plans and their limits");

        var users = app.MapGroup("/api/users")
            .WithTags("Users")
            // User management is an administrator concern, so this group asks
            // for the admin policy rather than the member one.
            .RequireAuthorization(Policies.TenantAdmin);

        users.MapGet("/", async (IUserService service, CancellationToken cancellationToken) =>
            {
                var result = await service.ListAsync(cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("ListUsers")
            .WithSummary("Lists the users of the current tenant");

        users.MapPost("/", async (
                CreateUserRequest request,
                IUserService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.CreateAsync(request, cancellationToken);

                return result.ToCreatedResult(user => $"/api/users/{user.Id}");
            })
            .WithName("CreateUser")
            .WithSummary("Adds a user to the current tenant");

        users.MapPut("/{id:guid}/role", async (
                Guid id,
                ChangeRoleRequest request,
                IUserService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.ChangeRoleAsync(id, request, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("ChangeUserRole")
            .WithSummary("Changes a user's role");

        users.MapDelete("/{id:guid}", async (
                Guid id,
                IUserService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.DeactivateAsync(id, cancellationToken);

                return result.ToHttpResult();
            })
            .WithName("DeactivateUser")
            .WithSummary("Deactivates a user");

        return app;
    }
}
