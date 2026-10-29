using TradeNest.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TradeNest.Api.Services;

// Read a real mapped table instead of SELECT 1: a reachable but empty SQLite
// file must not count as a usable application database. No account data is read.
public sealed class DatabaseReadinessCheck(TradeNestContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await db.Businesses.AsNoTracking().Select(business => business.Id).Take(1).ToListAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Responses and reports deliberately omit exception messages, database
            // paths, connection strings, SQL, and credentials.
            return HealthCheckResult.Unhealthy("Database is unavailable.");
        }
    }
}
