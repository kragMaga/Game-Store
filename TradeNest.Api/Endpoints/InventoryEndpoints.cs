using System.Security.Claims;
using TradeNest.Api.Dtos;
using TradeNest.Api.Services;

namespace TradeNest.Api.Endpoints;

public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this WebApplication app)
    {
        // BusinessErrorFilter translates intentional domain rejections. Membership
        // and per-operation admin authority are resolved inside InventoryService.
        var group = app.MapGroup("/businesses/{businessId:guid}").WithTags("Inventory")
            .RequireAuthorization().WithParameterValidation().AddEndpointFilter<BusinessErrorFilter>();
        group.MapGet("/categories", async (Guid businessId, InventoryService service, ClaimsPrincipal user, int page = 1, int pageSize = 20) =>
            Results.Ok(await service.CategoriesAsync(businessId, UserId(user), page, pageSize)));
        Sensitive(group.MapPost("/categories", async (Guid businessId, CreateCategoryDto request, InventoryService service, ClaimsPrincipal user) =>
            Results.Ok(await service.CreateCategoryAsync(businessId, UserId(user), request))));
        // Master lists belong to this business; all changes require current admin
        // authority plus recent authentication, just like product configuration.
        Sensitive(group.MapPut("/categories/{id:guid}", async (Guid businessId, Guid id, UpdateMasterDataDto request, InventoryService service, ClaimsPrincipal user) =>
        { await service.UpdateMasterAsync(businessId, id, UserId(user), request, true); return Results.NoContent(); }));
        group.MapGet("/units-of-measure", async (Guid businessId, InventoryService service, ClaimsPrincipal user, int page = 1, int pageSize = 20) =>
            Results.Ok(await service.MeasuresAsync(businessId, UserId(user), page, pageSize)));
        Sensitive(group.MapPost("/units-of-measure", async (Guid businessId, CreateUnitOfMeasureDto request, InventoryService service, ClaimsPrincipal user) =>
            Results.Ok(await service.CreateMeasureAsync(businessId, UserId(user), request))));
        Sensitive(group.MapPut("/units-of-measure/{id:guid}", async (Guid businessId, Guid id, UpdateMasterDataDto request, InventoryService service, ClaimsPrincipal user) =>
        { await service.UpdateMasterAsync(businessId, id, UserId(user), request, false); return Results.NoContent(); }));
        group.MapGet("/products", async (Guid businessId, InventoryService service, ClaimsPrincipal user,
            int page = 1, int pageSize = 20, string? search = null, bool lowStockOnly = false) =>
                Results.Ok(await service.ProductsAsync(businessId, UserId(user), page, pageSize, search, lowStockOnly)));
        Sensitive(group.MapPost("/products", async (Guid businessId, CreateInventoryProductDto request, InventoryService service, ClaimsPrincipal user) =>
        {
            var product = await service.CreateProductAsync(businessId, UserId(user), request);
            return Results.Created($"/businesses/{businessId}/products/{product.Id}", product);
        }));
        group.MapGet("/products/{productId:guid}", async (Guid businessId, Guid productId, InventoryService service, ClaimsPrincipal user) =>
            Results.Ok(await service.GetProductAsync(businessId, productId, UserId(user))));
        Sensitive(group.MapPut("/products/{productId:guid}", async (Guid businessId, Guid productId, UpdateInventoryProductDto request,
            InventoryService service, ClaimsPrincipal user) =>
        {
            await service.UpdateProductAsync(businessId, productId, UserId(user), request);
            return Results.NoContent();
        }));
        group.MapGet("/products/{productId:guid}/units", async (Guid businessId, Guid productId, InventoryService service,
            ClaimsPrincipal user, int page = 1, int pageSize = 20) =>
                Results.Ok(await service.UnitsAsync(businessId, productId, UserId(user), page, pageSize)));
        Sensitive(group.MapPost("/products/{productId:guid}/units", async (Guid businessId, Guid productId, CreateProductUnitDto request,
            InventoryService service, ClaimsPrincipal user) => Results.Ok(await service.CreateUnitAsync(businessId, productId, UserId(user), request))));
        Sensitive(group.MapPost("/products/{productId:guid}/opening-stock", async (Guid businessId, Guid productId, OpeningStockDto request,
            InventoryService service, ClaimsPrincipal user) => Results.Ok(await service.OpeningStockAsync(businessId, productId, UserId(user), request))));
        group.MapGet("/products/{productId:guid}/movements", async (Guid businessId, Guid productId, InventoryService service,
            ClaimsPrincipal user, int page = 1, int pageSize = 20) =>
                Results.Ok(await service.MovementsAsync(businessId, productId, UserId(user), page, pageSize)));
    }

    // Opening balances/product configuration can substantially change inventory.
    // Require fresh proof and apply the existing baseline in addition to admin checks.
    private static void Sensitive(RouteHandlerBuilder endpoint) =>
        endpoint.RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();
    private static int UserId(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
