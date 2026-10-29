using System.Security.Claims;
using TradeNest.Api.Dtos;
using TradeNest.Api.Services;

namespace TradeNest.Api.Endpoints;

public static class CustomerLedgerEndpoints
{
    public static void MapCustomerLedgerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/businesses/{businessId:guid}").WithTags("Customers and payments")
            .RequireAuthorization().WithParameterValidation().AddEndpointFilter<BusinessErrorFilter>();
        group.MapGet("/customers", async (Guid businessId, ClaimsPrincipal user, CustomerLedgerService service,
            int page = 1, int pageSize = 20, string? search = null, bool includeInactive = false) =>
                Results.Ok(await service.ListAsync(businessId, UserId(user), page, pageSize, search, includeInactive)));
        Admin(group.MapPost("/customers", async (Guid businessId, CreateCustomerDto request, ClaimsPrincipal user, CustomerLedgerService service) =>
        {
            var customer = await service.CreateAsync(businessId, UserId(user), request);
            return Results.Created($"/businesses/{businessId}/customers?search={Uri.EscapeDataString(customer.Code)}", customer);
        }));
        Admin(group.MapPut("/customers/{customerId:guid}", async (Guid businessId, Guid customerId, UpdateCustomerDto request,
            ClaimsPrincipal user, CustomerLedgerService service) =>
        {
            await service.UpdateAsync(businessId, customerId, UserId(user), request);
            return Results.NoContent();
        }));
        Admin(group.MapPost("/customers/{customerId:guid}/opening-debt", async (Guid businessId, Guid customerId,
            OpeningCustomerDebtDto request, ClaimsPrincipal user, CustomerLedgerService service) =>
                Results.Ok(await service.OpeningAsync(businessId, customerId, UserId(user), request))));
        // Late payment access is resolved from LIVE per-business membership, not
        // a global role or a client-supplied flag. No recent-proof requirement for
        // routine permitted receipts; retain the normal inventory-write budget.
        group.MapPost("/customers/{customerId:guid}/payments", async (Guid businessId, Guid customerId,
            RecordCustomerPaymentDto request, ClaimsPrincipal user, CustomerLedgerService service) =>
                Results.Ok(await service.PayAsync(businessId, customerId, UserId(user), request)))
            .RequireRateLimiting("inventory-write");
        group.MapPost("/customers/{customerId:guid}/payments/preview", async (Guid businessId, Guid customerId,
            RecordCustomerPaymentDto request, ClaimsPrincipal user, CustomerLedgerService service) =>
            Results.Ok(await service.PreviewPaymentAsync(businessId, customerId, UserId(user), request))).RequireRateLimiting("inventory-write");
        group.MapGet("/customers/{customerId:guid}/statement", async (Guid businessId, Guid customerId,
            ClaimsPrincipal user, CustomerLedgerService service, int page = 1, int pageSize = 20) =>
                Results.Ok(await service.StatementAsync(businessId, customerId, UserId(user), page, pageSize)));
        group.MapGet("/customers/{customerId:guid}/payments", async (Guid businessId, Guid customerId,
            ClaimsPrincipal user, CustomerLedgerService service, int page = 1, int pageSize = 20, DateOnly? from = null,
            DateOnly? to = null, int? staffUserId = null, string? search = null) =>
                Results.Ok(await service.PaymentsAsync(businessId, customerId, UserId(user), page, pageSize, new(from, to, staffUserId, search))));
        Admin(group.MapPost("/customer-payments/{paymentId:guid}/reverse", async (Guid businessId, Guid paymentId,
            ReverseCustomerPaymentDto request, ClaimsPrincipal user, CustomerLedgerService service) =>
                Results.Ok(await service.ReverseAsync(businessId, paymentId, UserId(user), request))));
        group.MapGet("/customer-payments/{paymentId:guid}", async (Guid businessId, Guid paymentId,
            ClaimsPrincipal user, CustomerLedgerService service) => Results.Ok(await service.PaymentDetailAsync(businessId, paymentId, UserId(user))));
        group.MapGet("/reports/customer-balances", async (Guid businessId, ClaimsPrincipal user, CustomerLedgerService service,
            int page = 1, int pageSize = 20) => Results.Ok(await service.DebtorsAsync(businessId, UserId(user), page, pageSize)));
    }

    // Configuration/correction operations require recent proof in addition to the
    // administrative guard inside the service and normal auth rate limiting.
    private static void Admin(RouteHandlerBuilder route) => route.RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();
    private static int UserId(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
