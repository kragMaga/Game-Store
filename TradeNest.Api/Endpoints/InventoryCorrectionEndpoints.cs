using System.Security.Claims;
using TradeNest.Api.Dtos;
using TradeNest.Api.Services;
namespace TradeNest.Api.Endpoints;

public static class InventoryCorrectionEndpoints
{
    public static void MapInventoryCorrectionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/businesses/{businessId:guid}").WithTags("Corrections")
            .RequireAuthorization().WithParameterValidation().AddEndpointFilter<BusinessErrorFilter>();
        // Recent proof protects stock/financial corrections, while the service
        // independently enforces live Owner/Admin membership on every call.
        group.MapPost("/transactions/{transactionId:guid}/returns", async (Guid businessId, Guid transactionId,
            ClaimsPrincipal user, ReturnInventoryDto request, InventoryCorrectionService service) =>
            Results.Ok(await service.ReturnAsync(businessId, transactionId, Id(user), request)))
            .RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();
        foreach (var count in new[] { false, true })
        {
            var isCount = count;
            group.MapPost(isCount ? "/products/{productId:guid}/stock-counts" : "/products/{productId:guid}/stock-adjustments",
                async (Guid businessId, Guid productId, ClaimsPrincipal user, AdjustStockDto request, InventoryCorrectionService service) =>
                    Results.Ok(await service.StockAsync(businessId, productId, Id(user), isCount, request)))
                .RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();
        }
        group.MapPost("/customer-charges/{chargeId:guid}/correct-opening-debt", async (Guid businessId, Guid chargeId,
            ClaimsPrincipal user, CorrectOpeningDebtDto request, InventoryCorrectionService service) =>
                Results.Ok(await service.OpeningDebtAsync(businessId, chargeId, Id(user), request)))
            .RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();
        group.MapGet("/corrections/{correctionId:guid}", async (Guid businessId, Guid correctionId, ClaimsPrincipal user, InventoryCorrectionService service) =>
            Results.Ok(await service.DetailAsync(businessId, correctionId, Id(user))));
        group.MapGet("/corrections", async (Guid businessId, ClaimsPrincipal user, InventoryCorrectionService service,
            int page = 1, int pageSize = 20) => Results.Ok(await service.ListAsync(businessId, Id(user), page, pageSize)));
    }
    private static int Id(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
