namespace TradeNest.Api.Services;

// This is an operator declaration, not automatic discovery of other replicas.
// Multi-instance hosts need a gateway that enforces aggregate authentication
// budgets; local ASP.NET limiters alone give each replica a separate allowance.
public sealed class DeploymentOptions
{
    public bool MultipleInstances { get; set; }
    public bool SharedRateLimitingEnforced { get; set; }
}
