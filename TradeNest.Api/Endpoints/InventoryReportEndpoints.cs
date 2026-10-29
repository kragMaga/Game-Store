using System.Security.Claims;
using TradeNest.Api.Services;
namespace TradeNest.Api.Endpoints;

public static class InventoryReportEndpoints
{
    public static void MapInventoryReportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/businesses/{businessId:guid}/reports").RequireAuthorization().WithTags("Reports and exports")
            .AddEndpointFilter<BusinessErrorFilter>();
        // Separate category routes preserve existing document-level report contracts.
        group.MapGet("/categories/{report}", async (Guid businessId, string report, ClaimsPrincipal user, InventoryReportService service,
            DateOnly? from = null, DateOnly? to = null, Guid? categoryId = null, int page = 1, int pageSize = 100) =>
                Results.Ok(await service.CategoriesAsync(businessId, Id(user), report, from, to, categoryId, page, pageSize)));
        group.MapGet("/categories/{report}/export.xlsx", async (Guid businessId, string report, ClaimsPrincipal user, InventoryReportService service,
            DateOnly? from = null, DateOnly? to = null, Guid? categoryId = null) => Results.File(
                InventoryWorkbook.Create(await service.CategoriesAsync(businessId, Id(user), report, from, to, categoryId, export: true)),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"category-{report}.xlsx"))
            .RequireRateLimiting("inventory-write");
        group.MapGet("/summary", async (Guid businessId, ClaimsPrincipal user, InventoryReportService service, DateOnly? from = null, DateOnly? to = null) =>
            Results.Ok(await service.SummaryAsync(businessId, Id(user), from, to)));
        group.MapGet("/tables/{report}", async (Guid businessId, string report, ClaimsPrincipal user, InventoryReportService service,
            DateOnly? from = null, DateOnly? to = null, int page = 1, int pageSize = 100) =>
                Results.Ok(await service.TableAsync(businessId, Id(user), report, from, to, page, pageSize)));
        // GET exports share the posting budget to bound repeated expensive reads.
        group.MapGet("/exports/{report}.xlsx", async (Guid businessId, string report, ClaimsPrincipal user, InventoryReportService service,
            DateOnly? from = null, DateOnly? to = null) => Results.File(
                InventoryWorkbook.Create(await service.TableAsync(businessId, Id(user), report, from, to, export: true)),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{report}.xlsx"))
            .RequireRateLimiting("inventory-write");
    }
    private static int Id(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
