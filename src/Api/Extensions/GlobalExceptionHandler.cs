using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SaasMultiTenant.Application.Abstractions;
using SaasMultiTenant.Domain.Common;
using SaasMultiTenant.Domain.Tenants;
using SaasMultiTenant.Infrastructure.Persistence;

namespace SaasMultiTenant.Api.Extensions;

/// <summary>
/// The last line of defence, and the place cross tenant attempts are recorded.
///
/// A <see cref="CrossTenantAccessException"/> reaching here means the write
/// guard in the DbContext stopped something the application layer should never
/// have attempted. It is logged at warning level with both tenant ids, because
/// it is the signal an operator wants an alert on, and answered with a plain
/// 403 that says nothing about the row that was touched.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            CrossTenantAccessException crossTenant => HandleCrossTenant(crossTenant),
            PlanLimitExceededException planLimit => new ProblemDetails
            {
                Status = StatusCodes.Status402PaymentRequired,
                Title = "Plan limit reached",
                Detail = planLimit.Message,
            },
            DomainException domain => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request",
                Detail = domain.Message,
            },
            MissingTenantException => HandleMissingTenant(httpContext),
            _ => null,
        };

        if (problem is null)
        {
            _logger.LogError(exception, "Unhandled exception on {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);

            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Unexpected error",
                Detail = "The request could not be completed.",
            };
        }

        problem.Instance = httpContext.Request.Path;

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);

        return true;
    }

    private ProblemDetails HandleCrossTenant(CrossTenantAccessException exception)
    {
        _logger.LogWarning(
            "Cross tenant write blocked. Tenant {ActingTenantId} attempted to modify {EntityName} {EntityId} "
            + "owned by tenant {OwnerTenantId}",
            exception.ActingTenantId,
            exception.EntityName,
            exception.EntityId,
            exception.OwnerTenantId);

        return new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Not allowed",

            // Deliberately vague. Confirming which entity was touched would
            // tell the caller that an id they guessed belongs to a real row in
            // another tenant.
            Detail = "This operation is not allowed.",
        };
    }

    private ProblemDetails HandleMissingTenant(HttpContext httpContext)
    {
        // A 500 rather than a 401: authorization should have rejected this
        // request before any data access, so reaching here is a configuration
        // mistake on the server, not a missing credential on the client.
        _logger.LogError(
            "Tenant scoped work ran without a tenant on {Method} {Path}. An endpoint is likely missing its "
            + "authorization policy.",
            httpContext.Request.Method,
            httpContext.Request.Path);

        return new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Unexpected error",
            Detail = "The request could not be completed.",
        };
    }
}
