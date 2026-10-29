using TradeNest.Api.Services;

namespace TradeNest.Api.Endpoints;

// Attach with AddEndpointFilter<RecentAuthenticationFilter>() on sensitive routes.
// Normal read-only routes do not need this extra check after bearer authentication.
public sealed class RecentAuthenticationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Authorization still runs first. A valid bearer token alone is insufficient
        // for sensitive changes once the original sign-in is more than five minutes old.
        var service = context.HttpContext.RequestServices.GetRequiredService<RecentAuthenticationService>();
        // Stop before the handler can mutate anything. A 403 tells an authenticated
        // client to obtain fresh proof; it does not sign the existing session out.
        return await service.IsRecentAsync(context.HttpContext)
            ? await next(context)
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Recent authentication required",
                detail: "Sign in again, or reauthenticate and send the returned proof in X-Reauthentication-Token.");
    }
}
