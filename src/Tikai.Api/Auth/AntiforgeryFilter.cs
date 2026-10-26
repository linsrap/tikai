using Microsoft.AspNetCore.Antiforgery;

namespace Tikai.Api.Auth;

/// <summary>
/// Requires a valid antiforgery token (X-XSRF-TOKEN header matching the antiforgery cookie) on every
/// unsafe request. Cookie auth would otherwise let other sites trigger actions with the user's cookie.
/// </summary>
internal sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing or invalid antiforgery token",
                    detail: "Fetch GET /api/auth/antiforgery and send the XSRF-TOKEN cookie value in the X-XSRF-TOKEN header.");
            }
        }
        return await next(context);
    }
}
