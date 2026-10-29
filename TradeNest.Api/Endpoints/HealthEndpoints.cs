using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace TradeNest.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapOperationalHealth(this WebApplication app)
    {
        // Liveness only tests the HTTP process. Restarting a healthy process during
        // a database outage would make recovery harder, so it skips dependencies.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous().ExcludeFromDescription();
        // Readiness gates traffic on database availability. Built-in middleware
        // returns 503 for failure, a terse status body, and no-cache headers.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
            { Predicate = registration => registration.Tags.Contains("ready") })
            .AllowAnonymous().ExcludeFromDescription();
    }
}
