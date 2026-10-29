using System.Security.Claims;
using TradeNest.Api.Services;
namespace TradeNest.Api.Endpoints;

public static class BusinessOperationsEndpoints
{
    public static void MapBusinessOperationsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/businesses/{businessId:guid}").RequireAuthorization().WithTags("Business insights and onboarding")
            .WithParameterValidation().AddEndpointFilter<BusinessErrorFilter>();
        group.MapGet("/dashboard", async (Guid businessId, ClaimsPrincipal user, BusinessInsightService service) => Results.Ok(await service.DashboardAsync(businessId, Id(user))));
        group.MapGet("/reports/debt-ageing", async (Guid businessId, ClaimsPrincipal user, BusinessInsightService service, int page = 1, int pageSize = 20) =>
            Results.Ok(await service.AgeingAsync(businessId, Id(user), page, pageSize)));
        group.MapGet("/reports/exports/debt-ageing.xlsx", async (Guid businessId, ClaimsPrincipal user, BusinessInsightService service) =>
            Results.File(InventoryWorkbook.Create(await service.AgeingExportAsync(businessId, Id(user))), Excel, "debt-ageing.xlsx"))
            .RequireRateLimiting("inventory-write");
        group.MapGet("/reports/stock-reconciliation", async (Guid businessId, ClaimsPrincipal user, BusinessInsightService service, DateOnly from, DateOnly to, int page = 1, int pageSize = 20) =>
            Results.Ok(await service.ReconciliationAsync(businessId, Id(user), from, to, page, pageSize)));
        group.MapGet("/reports/exports/stock-reconciliation.xlsx", async (Guid businessId, ClaimsPrincipal user, BusinessInsightService service, DateOnly from, DateOnly to) =>
            Results.File(InventoryWorkbook.Create(await service.ReconciliationAsync(businessId, Id(user), from, to, 1, 100, export: true)), Excel, "stock-reconciliation.xlsx"))
            .RequireRateLimiting("inventory-write");
        group.MapGet("/notifications", async (Guid businessId, ClaimsPrincipal user, BusinessNotificationService service, int page = 1, int pageSize = 20) =>
            Results.Ok(await service.HistoryAsync(businessId, Id(user), page, pageSize)));
        Secure(group.MapPost("/notifications/{notificationId:guid}/retry", async (Guid businessId, Guid notificationId, ClaimsPrincipal user, BusinessNotificationService service) =>
        { await service.RetryAsync(businessId, notificationId, Id(user)); return Results.Accepted(); }));
        group.MapGet("/settings/debt-reminders", async (Guid businessId, ClaimsPrincipal user, BusinessNotificationService service) => Results.Ok(await service.SettingsAsync(businessId, Id(user))));
        Secure(group.MapPut("/settings/debt-reminders", async (Guid businessId, DebtReminderSettingsDto request, ClaimsPrincipal user, BusinessNotificationService service) =>
        { await service.SettingsAsync(businessId, Id(user), request); return Results.NoContent(); }));
        group.MapGet("/imports/{kind}/template.xlsx", async (Guid businessId, string kind, ClaimsPrincipal user, InventoryImportService service) =>
            Results.File(await service.TemplateAsync(businessId, Id(user), kind), Excel, kind + ".xlsx"));
        // Raw XLSX request bodies avoid multipart/antiforgery coupling in this
        // bearer-auth API. Read at most the file limit plus one byte, even when a
        // malicious chunked request omits Content-Length.
        Secure(group.MapPost("/imports/{kind}/preview", async (Guid businessId, string kind, HttpContext context, InventoryImportService service) =>
        {
            if (context.Request.ContentLength > InventorySpreadsheet.MaxFileBytes) return Results.Problem(statusCode: 413, title: "Workbook exceeds 2 MB");
            using var file = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (file.Length + read > InventorySpreadsheet.MaxFileBytes) return Results.Problem(statusCode: 413, title: "Workbook exceeds 2 MB");
                file.Write(buffer, 0, read);
            }
            return Results.Ok(await service.PreviewAsync(businessId, Id(context.User), kind, file.ToArray()));
        }));
        Secure(group.MapPost("/imports/commit", async (Guid businessId, ClaimsPrincipal user, CommitInventoryImportDto request, InventoryImportService service) =>
            Results.Ok(await service.CommitAsync(businessId, Id(user), request.PreviewToken))));
    }
    private const string Excel = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static void Secure(RouteHandlerBuilder route) => route.RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();
    private static int Id(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
