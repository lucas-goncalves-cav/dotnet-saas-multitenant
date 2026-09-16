using Microsoft.AspNetCore.Mvc;
using SaasMultiTenant.Application.Common;

namespace SaasMultiTenant.Api.Extensions;

/// <summary>
/// Translates an application <see cref="Result"/> into an HTTP response.
///
/// Doing it in one place keeps status codes consistent, and keeps the services
/// free of any knowledge of HTTP.
/// </summary>
public static class ResultExtensions
{
    public static IResult ToHttpResult<TValue>(this Result<TValue> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);

    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? Results.NoContent() : Problem(result.Error);

    public static IResult ToCreatedResult<TValue>(this Result<TValue> result, Func<TValue, string> location) =>
        result.IsSuccess ? Results.Created(location(result.Value), result.Value) : Problem(result.Error);

    private static IResult Problem(Error error)
    {
        var (status, title) = error.Code switch
        {
            "not_found" => (StatusCodes.Status404NotFound, "Resource not found"),
            "validation" => (StatusCodes.Status400BadRequest, "Invalid request"),
            "conflict" => (StatusCodes.Status409Conflict, "Conflict"),
            "unauthorized" => (StatusCodes.Status401Unauthorized, "Authentication failed"),
            "forbidden" => (StatusCodes.Status403Forbidden, "Not allowed"),

            // 402 rather than 403, because the request is well formed and the
            // caller is allowed to make it. What is missing is a bigger plan,
            // and that distinction is what tells a client to show an upgrade
            // prompt instead of an error.
            "plan_limit" => (StatusCodes.Status402PaymentRequired, "Plan limit reached"),
            _ => (StatusCodes.Status400BadRequest, "Request failed"),
        };

        return Results.Problem(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = error.Message,
            Extensions = { ["code"] = error.Code },
        });
    }
}
