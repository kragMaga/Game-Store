using System.Security.Claims;
using TradeNest.Api.Dtos;
using TradeNest.Api.Services;

namespace TradeNest.Api.Endpoints;

public static class InventoryTransactionEndpoints
{
    public static void MapInventoryTransactionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/businesses/{businessId:guid}").WithTags("Purchases and sales")
            .RequireAuthorization().WithParameterValidation().AddEndpointFilter<BusinessErrorFilter>();
        // Separate routes fix the operation kind on the server. A client cannot
        // turn a sale into a receipt by injecting a Kind field into JSON.
        MapDocuments(group, "purchases", "Purchase");
        MapDocuments(group, "sales", "Sale");
        group.MapGet("/suppliers", async (Guid businessId, ClaimsPrincipal user, InventoryTransactionService service,
            int page = 1, int pageSize = 20) => Results.Ok(await service.SuppliersAsync(businessId, UserId(user), page, pageSize)));
        group.MapPost("/suppliers", async (Guid businessId, CreateSupplierDto request, ClaimsPrincipal user, InventoryTransactionService service) =>
            Results.Ok(await service.CreateSupplierAsync(businessId, UserId(user), request)))
            .RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();
    }

    private static void MapDocuments(RouteGroupBuilder group, string route, string kind)
    {
        group.MapPost("/" + route, async (Guid businessId, RecordInventoryTransactionDto request,
            ClaimsPrincipal user, InventoryTransactionService service) =>
                Results.Ok(await service.RecordAsync(businessId, UserId(user), kind, request)))
            .RequireRateLimiting("inventory-write");
        group.MapPost("/" + route + "/preview", async (Guid businessId, RecordInventoryTransactionDto request, ClaimsPrincipal user, InventoryTransactionService service) =>
            Results.Ok(await service.PreviewAsync(businessId, UserId(user), kind, request))).RequireRateLimiting("inventory-write");
        group.MapGet("/" + route, async (Guid businessId, ClaimsPrincipal user, InventoryTransactionService service,
            int page = 1, int pageSize = 20, DateOnly? from = null, DateOnly? to = null, Guid? productId = null,
            Guid? customerId = null, Guid? supplierId = null, int? staffUserId = null, string? search = null) =>
                Results.Ok(await service.ListAsync(businessId, UserId(user), kind, page, pageSize, new(from, to, productId, customerId, supplierId, staffUserId, search))));
        group.MapGet("/" + route + "/{transactionId:guid}", async (Guid businessId, Guid transactionId,
            ClaimsPrincipal user, InventoryTransactionService service) => Results.Ok(await service.GetAsync(businessId, transactionId, UserId(user), kind)));
    }

    private static int UserId(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
