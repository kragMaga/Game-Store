using System.Security.Claims;
using TradeNest.Api.Services;
namespace TradeNest.Api.Endpoints;

public static class CustomerDocumentEndpoints
{
    public static void MapCustomerDocumentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/businesses/{businessId:guid}").RequireAuthorization().WithTags("Documents and onboarding")
            .AddEndpointFilter<BusinessErrorFilter>();
        group.MapGet("/onboarding-status", async (Guid businessId, ClaimsPrincipal user, OnboardingStatusService service) =>
            Results.Ok(await service.GetAsync(businessId, Id(user))));
        group.MapGet("/customers/{customerId:guid}/statement.xlsx", async (Guid businessId, Guid customerId, ClaimsPrincipal user, CustomerDocumentService service) =>
        {
            var result = await service.StatementAsync(businessId, customerId, Id(user));
            return Results.File(InventoryWorkbook.Create(result.Table), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "customer-statement.xlsx");
        }).RequireRateLimiting("inventory-write");
        group.MapGet("/customers/{customerId:guid}/statement.html", async (Guid businessId, Guid customerId, HttpContext context, CustomerDocumentService service) =>
        {
            var result = await service.StatementAsync(businessId, customerId, Id(context.User)); ProtectHtml(context);
            return Results.File(CustomerDocumentService.RenderHtml(result.Table, result.BusinessName, result.CustomerName,
                "Current customer ledger: debits increase debt; credits reduce it. Includes recorded payments, corrections and refunds."), "text/html; charset=utf-8", "customer-statement.html");
        }).RequireRateLimiting("inventory-write");
        group.MapGet("/customer-payments/{paymentId:guid}/receipt.html", async (Guid businessId, Guid paymentId, HttpContext context, CustomerDocumentService service) =>
        {
            var bytes = await service.ReceiptAsync(businessId, paymentId, Id(context.User)); ProtectHtml(context);
            return Results.File(bytes, "text/html; charset=utf-8", "payment-receipt.html");
        }).RequireRateLimiting("inventory-write");
    }
    private static int Id(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static void ProtectHtml(HttpContext context)
    {
        // Downloads are fully escaped; CSP is another boundary if opened inline.
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
    }
}
