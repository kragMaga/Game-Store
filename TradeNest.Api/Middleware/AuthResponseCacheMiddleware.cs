namespace TradeNest.Api.Middleware;

// Apply one cache rule to the entire authentication area instead of relying on
// every endpoint author to remember headers when returning secrets or account data.
public sealed class AuthResponseCacheMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        // The provider callback lives outside /auth but still carries sign-in data.
        if (context.Request.Path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase) ||
            // Business settings, staff details, and invitation responses are private
            // tenant data; give early errors the same no-store protection too.
            context.Request.Path.StartsWithSegments("/businesses", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/business-invitations", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.Equals("/signin-oidc", StringComparison.OrdinalIgnoreCase))
        {
            // Register before auth/rate-limit middleware so even rejected requests,
            // MFA challenges, setup keys, and recovery codes cannot be cached.
            context.Response.OnStarting(() =>
            {
                // OnStarting runs just before headers are sent, including when an
                // earlier middleware short-circuits the request before its handler.
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.Pragma = "no-cache";
                return Task.CompletedTask;
            });
        }
        // This middleware sets response policy only; access checks happen downstream.
        return next(context);
    }
}
