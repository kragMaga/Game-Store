using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class UserWorkflowTests
{
    [Fact]
    public async Task Previews_roll_back_stock_ledger_and_numbers_then_posting_and_retries_keep_stable_numbers()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var request = Sale(d, "number-sale-001", 1, 10);
        var preview = await d.Sales.PreviewAsync(d.Business, d.Owner, "Sale", request);
        Assert.False(preview.AlreadyRecorded); Assert.Null(preview.Entry.Transaction.DocumentNumber); Assert.Equal(Guid.Empty, preview.Entry.Transaction.Id);
        Assert.Equal(10, preview.Entry.Transaction.Total); Assert.Empty(await d.Db.InventoryTransactions.ToListAsync()); Assert.Empty(await d.Db.DocumentSequences.ToListAsync());
        Assert.Equal(20, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
        var posted = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", request);
        Assert.Equal("SALE-000001", posted.Transaction.DocumentNumber); Assert.Equal("PAY-000001", (await d.Db.CustomerPayments.AsNoTracking().SingleAsync()).DocumentNumber);
        Assert.Equal(posted.Transaction.DocumentNumber, (await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", request)).Transaction.DocumentNumber);
        Assert.True((await d.Sales.PreviewAsync(d.Business, d.Owner, "Sale", request)).AlreadyRecorded);
        await Error(409, () => d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "too-large-sale", 100, 1)));
        var second = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "number-sale-002", 1, 10));
        Assert.Equal("SALE-000002", second.Transaction.DocumentNumber);
        using var client = await Login(f, "Admin", "Bootstrap-Cedar-River-593!");
        var concurrent = await Task.WhenAll(client.PostAsJsonAsync($"/businesses/{d.Business}/sales", Sale(d, "concurrent-number-a", 1, 10)),
            client.PostAsJsonAsync($"/businesses/{d.Business}/sales", Sale(d, "concurrent-number-b", 1, 10)));
        var numbers = new List<string>();
        foreach (var response in concurrent) { response.EnsureSuccessStatusCode(); numbers.Add((await response.Content.ReadFromJsonAsync<InventoryTransactionDto>())!.Transaction.DocumentNumber!); response.Dispose(); }
        Assert.Equal(new[] { "SALE-000003", "SALE-000004" }, numbers.OrderBy(n => n));
        var other = await d.Businesses.CreateAsync(d.Owner, new("Other Tenant", "NGN", "other@example.test", "Africa/Lagos"));
        var p = await d.Inventory.CreateProductAsync(other.Id, d.Owner, new("Other product", "OTHER", "Each", false));
        var unit = (await d.Inventory.UnitsAsync(other.Id, p.Id, d.Owner, 1, 20)).Items.Single();
        var purchase = await d.Sales.RecordAsync(other.Id, d.Owner, "Purchase", new(Today, [new(p.Id, unit.Id, 2, 10)], "other-purchase-001"));
        Assert.Equal("PUR-000001", purchase.Transaction.DocumentNumber);
    }

    [Fact]
    public async Task Duplicate_warnings_precede_posting_and_search_filters_do_not_widen_staff_visibility()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        await d.Ledger.UpdateAsync(d.Business, d.Customer, d.Owner, new("Café Customer", true));
        var ownerSale = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "owner-search-001", 1, 10) with { Reference = "External-ABC" });
        Assert.Single((await d.Sales.ListAsync(d.Business, d.Owner, "Sale", 1, 20, new(Search: "Café"))).Items);
        var request = Sale(d, "duplicate-new-key", 1, 10) with { Reference = "External-ABC" };
        Assert.Equal(ownerSale.Transaction.Id, Assert.Single((await d.Sales.PreviewAsync(d.Business, d.Owner, "Sale", request)).PossibleDuplicates).Id);
        var staff = await Staff(scope.ServiceProvider, d);
        Assert.Empty((await d.Sales.PreviewAsync(d.Business, staff.Id, "Sale", request)).PossibleDuplicates);
        var own = await d.Sales.RecordAsync(d.Business, staff.Id, "Sale", request);
        Assert.Single((await d.Sales.ListAsync(d.Business, d.Owner, "Sale", 1, 20, new(ProductId: d.Product, CustomerId: d.Customer, StaffUserId: staff.Id, Search: "external-abc"))).Items);
        Assert.Empty((await d.Sales.ListAsync(d.Business, staff.Id, "Sale", 1, 20, new(StaffUserId: d.Owner))).Items);
        Assert.Equal(own.Transaction.Id, Assert.Single((await d.Sales.ListAsync(d.Business, staff.Id, "Sale", 1, 20, new(Search: own.Transaction.DocumentNumber!.ToLowerInvariant()))).Items).Id);
        var literal = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "literal-search-001", 1, 10) with { Reference = "PO_10%" });
        Assert.Equal(literal.Transaction.Id, Assert.Single((await d.Sales.ListAsync(d.Business, d.Owner, "Sale", 1, 20, new(Search: "PO_10%"))).Items).Id);
        Assert.Empty((await d.Sales.ListAsync(d.Business, d.Owner, "Sale", 1, 20, new(Search: "PO_10%%"))).Items);
        await Error(400, () => d.Sales.ListAsync(d.Business, d.Owner, "Sale", 1, 20, new(Today, Today.AddDays(-1))));
        using var client = await Login(f, "Admin", "Bootstrap-Cedar-River-593!");
        using var filtered = await client.GetAsync($"/businesses/{d.Business}/sales?customerId={d.Customer}&staffUserId={staff.Id}&search=External-ABC");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode); Assert.Single((await filtered.Content.ReadFromJsonAsync<PagedResponseDto<InventoryTransactionSummaryDto>>())!.Items);
    }

    [Fact]
    public async Task Payment_previews_warn_without_allocating_debt_and_filters_find_receipt_numbers()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var charge = await d.Ledger.OpeningAsync(d.Business, d.Customer, d.Owner, new(100, Today, "Onboarding debt", "payment-opening-001"));
        var request = new RecordCustomerPaymentDto(20, Today, "Cash", [new(charge.Id, 20)], "preview-payment-001", "BankRef");
        var preview = await d.Ledger.PreviewPaymentAsync(d.Business, d.Customer, d.Owner, request);
        Assert.False(preview.AlreadyRecorded); Assert.Null(preview.Payment.DocumentNumber); Assert.Empty(await d.Db.CustomerPayments.ToListAsync());
        Assert.Equal(100, (await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Balance.Outstanding);
        var payment = await d.Ledger.PayAsync(d.Business, d.Customer, d.Owner, request);
        Assert.Equal("PAY-000001", payment.DocumentNumber);
        Assert.Single((await d.Ledger.PreviewPaymentAsync(d.Business, d.Customer, d.Owner, request with { IdempotencyKey = "preview-payment-002" })).PossibleDuplicates);
        Assert.Single((await d.Ledger.PaymentsAsync(d.Business, d.Customer, d.Owner, 1, 20, new(Search: "pay-000001"))).Items);
        var reversal = await d.Ledger.ReverseAsync(d.Business, payment.Id, d.Owner, new(Today, "Incorrect receipt", "number-reversal-001"));
        Assert.Equal("REV-000001", reversal.DocumentNumber);
        Assert.Empty((await d.Ledger.PreviewPaymentAsync(d.Business, d.Customer, d.Owner, request with { IdempotencyKey = "preview-payment-003" })).PossibleDuplicates);
    }

    [Fact]
    public async Task Statement_downloads_reconcile_payments_reversals_credits_and_refunds_and_escape_names()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var opening = await d.Ledger.OpeningAsync(d.Business, d.Customer, d.Owner, new(50, Today, "Opening debt", "statement-opening-001"));
        var sale = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "statement-sale-001", 4, 25) with { AmountPaid = 20 });
        var later = await d.Ledger.PayAsync(d.Business, d.Customer, d.Owner, new(30, Today, "Cash", [new(opening.Id, 30)], "statement-payment-001"));
        await d.Ledger.ReverseAsync(d.Business, later.Id, d.Owner, new(Today, "Wrong receipt", "statement-reversal-001"));
        await d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, new(Today, "One item returned", "statement-return-001", [new(sale.Lines.Single().Id, 1)]));
        var correction = await d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner,
            new(Today, "Return remaining items", "statement-return-002", ReverseRemaining: true, RefundAmount: 20, RefundMethod: "Cash"));
        Assert.Equal("COR-000002", correction.DocumentNumber);
        await d.Ledger.UpdateAsync(d.Business, d.Customer, d.Owner, new("<img src=x onerror=alert(1)>", true));
        var document = await d.Documents.StatementAsync(d.Business, d.Customer, d.Owner);
        Assert.Equal(8, document.Table.Rows.Count); Assert.Equal(50m, document.Table.Rows.Last()[7]);
        Assert.Equal(50, (await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Balance.Outstanding);
        var html = Encoding.UTF8.GetString(CustomerDocumentService.RenderHtml(document.Table, document.BusinessName, document.CustomerName, "Statement"));
        Assert.DoesNotContain("<img", html); Assert.Contains("&lt;img", html); Assert.Contains("Current balance: 50 NGN", html); Assert.Contains("SALE-000001", html); Assert.Contains("PAY-000002", html);
        var receipt = Encoding.UTF8.GetString(await d.Documents.ReceiptAsync(d.Business, later.Id, d.Owner)); Assert.Contains("REVERSED", receipt);
        using var client = await Login(f, "Admin", "Bootstrap-Cedar-River-593!");
        using var download = await client.GetAsync($"/businesses/{d.Business}/customers/{d.Customer}/statement.html");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.True(download.Headers.CacheControl!.NoStore);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType); Assert.True(download.Headers.Contains("Content-Security-Policy"));
        using var excel = await client.GetAsync($"/businesses/{d.Business}/customers/{d.Customer}/statement.xlsx"); Assert.Equal(HttpStatusCode.OK, excel.StatusCode);
    }

    [Fact]
    public async Task Staff_can_print_only_their_own_sale_receipt_and_onboarding_does_not_require_fictitious_balances()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var staff = await Staff(scope.ServiceProvider, d);
        var sale = await d.Sales.RecordAsync(d.Business, staff.Id, "Sale", Sale(d, "staff-receipt-001", 1, 10));
        var receipt = await d.Db.CustomerPayments.AsNoTracking().SingleAsync();
        Assert.NotEmpty(await d.Documents.ReceiptAsync(d.Business, receipt.Id, staff.Id));
        await Error(403, () => d.Documents.StatementAsync(d.Business, d.Customer, staff.Id));
        var otherStaff = await Staff(scope.ServiceProvider, d);
        await Error(403, () => d.Documents.ReceiptAsync(d.Business, receipt.Id, otherStaff.Id));
        var fresh = await d.Businesses.CreateAsync(d.Owner, new("Fresh business", "NGN", "fresh@example.test", "Africa/Lagos"));
        var status = JsonSerializer.SerializeToElement(await scope.ServiceProvider.GetRequiredService<OnboardingStatusService>().GetAsync(fresh.Id, d.Owner));
        Assert.False(status.GetProperty("HasProducts").GetBoolean()); Assert.Equal(0, status.GetProperty("OpeningDebtEntries").GetInt32()); Assert.NotEmpty(status.GetProperty("OptionalSteps").EnumerateArray());
        await Error(403, () => scope.ServiceProvider.GetRequiredService<OnboardingStatusService>().GetAsync(d.Business, staff.Id));
    }

    [Fact]
    public async Task Migration_backfills_existing_numbers_and_seeds_counters_without_changing_business_values()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var first = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "legacy-number-001", 1, 10));
        var second = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "legacy-number-002", 1, 10));
        d.Db.ChangeTracker.Clear(); var migrator = d.Db.GetService<IMigrator>();
        await migrator.MigrateAsync(d.Db.Database.GetMigrations().Single(m => m.EndsWith("_AddBusinessInsightsAndImports")));
        await migrator.MigrateAsync();
        Assert.Equal("SALE-000001", (await d.Sales.GetAsync(d.Business, first.Transaction.Id, d.Owner, "Sale")).Transaction.DocumentNumber);
        Assert.Equal("SALE-000002", (await d.Sales.GetAsync(d.Business, second.Transaction.Id, d.Owner, "Sale")).Transaction.DocumentNumber);
        var next = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "legacy-number-003", 1, 10)); Assert.Equal("SALE-000003", next.Transaction.DocumentNumber);
        Assert.Equal(17, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
        Assert.Equal(3, await d.Db.CustomerPayments.CountAsync());
    }

    private sealed record Data(TradeNestContext Db, InventoryService Inventory, InventoryTransactionService Sales, CustomerLedgerService Ledger,
        InventoryCorrectionService Corrections, CustomerDocumentService Documents, BusinessService Businesses, Guid Business, int Owner, Guid Product, Guid Unit, Guid Customer);
    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Africa/Lagos").DateTime);
    private static async Task<Data> Setup(IServiceProvider p)
    {
        var db = p.GetRequiredService<TradeNestContext>(); var owner = (await db.Users.SingleAsync()).Id; var businesses = p.GetRequiredService<BusinessService>();
        var b = await businesses.CreateAsync(owner, new("Workflow Store", "NGN", "stock@example.test", "Africa/Lagos")); var inventory = p.GetRequiredService<InventoryService>();
        var product = await inventory.CreateProductAsync(b.Id, owner, new("Workflow product", "FLOW", "Each", false));
        var unit = (await inventory.UnitsAsync(b.Id, product.Id, owner, 1, 20)).Items.Single();
        await inventory.OpeningStockAsync(b.Id, product.Id, owner, new(unit.Id, 20, Today.AddDays(-10), "Opening goods", "workflow-opening-001"));
        var ledger = p.GetRequiredService<CustomerLedgerService>(); var customer = await ledger.CreateAsync(b.Id, owner, new("MAIN", "Main customer"));
        return new(db, inventory, p.GetRequiredService<InventoryTransactionService>(), ledger, p.GetRequiredService<InventoryCorrectionService>(), p.GetRequiredService<CustomerDocumentService>(), businesses, b.Id, owner, product.Id, unit.Id, customer.Id);
    }
    private static RecordInventoryTransactionDto Sale(Data d, string key, decimal qty, decimal price) => new(Today, [new(d.Product, d.Unit, qty, price)], key, CustomerId: d.Customer, AmountPaid: qty * price, PaymentMethod: "Cash");
    private static async Task<User> Staff(IServiceProvider p, Data d)
    {
        var user = new User { UserName = "staff-" + Guid.NewGuid().ToString("N")[..8], Email = Guid.NewGuid().ToString("N") + "@example.test", EmailConfirmed = true};
        Assert.True((await p.GetRequiredService<UserManager<User>>().CreateAsync(user, "Cedar-Moon-River-593!")).Succeeded);
        d.Db.BusinessMemberships.Add(new() { BusinessId = d.Business, UserId = user.Id, Role = BusinessRole.Staff, JoinedAtUtc = DateTime.UtcNow }); await d.Db.SaveChangesAsync(); return user;
    }
    private static async Task Error(int code, Func<Task> work) => Assert.Equal(code, (await Assert.ThrowsAsync<BusinessRequestException>(work)).StatusCode);
    private static async Task<HttpClient> Login(TradeNestWebApplicationFactory f, string name, string password)
    { var client = f.CreateClient(); using var r = await client.PostAsJsonAsync("/auth/login", new { username = name, password }); r.EnsureSuccessStatusCode(); client.DefaultRequestHeaders.Authorization = new("Bearer", (await r.Content.ReadFromJsonAsync<AuthTokensDto>())!.AccessToken); return client; }
}
