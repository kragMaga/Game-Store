using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class InventoryCompletionTests
{
    [Fact]
    public async Task Partial_returns_credit_debt_then_refund_excess_payment_without_rewriting_originals()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope();
        var d = await Setup(scope.ServiceProvider);
        var sale = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, 5, 100, 200));
        var line = sale.Lines.Single();
        var request = new ReturnInventoryDto(Today, "Customer returned three items", "return-three-001", [new(line.Id, 3)]);
        var first = await d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, request);
        Assert.Equal(300, first.Amount); Assert.Equal(0, first.RefundAmount);
        Assert.Equal(first.Id, (await d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, request)).Id);
        Assert.Equal(0, (await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Balance.Outstanding);
        var remaining = new ReturnInventoryDto(Today, "Remaining items returned damaged", "return-rest-001", [new(line.Id, 2, false)], RefundAmount: 200, RefundMethod: "Cash");
        var final = await d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, remaining);
        Assert.Equal(200, final.RefundAmount);
        var balance = (await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Balance;
        Assert.Equal(0, balance.SalesTotal); Assert.Equal(0, balance.PaymentsReceived); Assert.Equal(0, balance.Outstanding);
        Assert.Equal(18, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
        Assert.Equal(50000, (await d.Db.InventoryTransactions.AsNoTracking().SingleAsync()).TotalMinor);
        var payment = await d.Db.CustomerPayments.AsNoTracking().SingleAsync();
        await Error(409, () => d.Ledger.ReverseAsync(d.Business, payment.Id, d.Owner, new(Today, "Cannot remove refunded receipt", "reverse-refunded-001")));
        await Error(409, () => d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, remaining with { IdempotencyKey = "return-too-much" }));
        var report = await d.Reports.TableAsync(d.Business, d.Owner, "customers", null, null);
        Assert.Equal(0m, report.Rows.Single()[7]);
        _ = await d.Reports.SummaryAsync(d.Business, d.Owner, null, null);
    }

    [Fact]
    public async Task Paid_walk_in_reversal_requires_exact_refund_and_rolls_back_on_invalid_amount()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var sale = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, 2, 50, 100) with { CustomerId = null });
        var request = new ReturnInventoryDto(Today, "Walk-in sale entered twice", "walkin-reversal-001", ReverseRemaining: true);
        await Error(409, () => d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, request));
        Assert.Equal(18, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
        Assert.Empty(await d.Db.InventoryCorrections.ToListAsync());
        var result = await d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, request with { RefundAmount = 100, RefundMethod = "Card" });
        Assert.Equal(100, result.Amount); Assert.Equal(100, result.RefundAmount);
        Assert.Equal(20, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
    }

    [Fact]
    public async Task Repeated_partial_returns_preserve_original_rounded_total_and_are_atomic_on_failure()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var sale = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, 3, 0.003m, 0));
        var id = sale.Lines.Single().Id;
        var first = await d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, new(Today, "Return first rounded item", "round-return-001", [new(id, 1)]));
        Assert.Equal(0, first.Amount);
        await d.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailCorrection BEFORE INSERT ON CustomerChargeAdjustments BEGIN SELECT RAISE(ABORT, 'failure'); END");
        var second = new ReturnInventoryDto(Today, "Return another rounded item", "round-return-002", [new(id, 1)]);
        await Assert.ThrowsAsync<DbUpdateException>(() => d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, second));
        Assert.Equal(18, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
        await d.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailCorrection");
        var two = await d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, second);
        var three = await d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, new(Today, "Return final rounded item", "round-return-003", [new(id, 1)]));
        Assert.Equal(0.01m, first.Amount + two.Amount + three.Amount);
        Assert.Equal(0, (await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Balance.Outstanding);
    }

    [Fact]
    public async Task Counts_and_loss_adjustments_reject_stale_balances_negative_stock_and_staff()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var count = new AdjustStockDto(17, 20, Today, "Physical stock count", "physical-count-001");
        var result = await d.Corrections.StockAsync(d.Business, d.Product, d.Owner, true, count);
        Assert.Equal(result.Id, (await d.Corrections.StockAsync(d.Business, d.Product, d.Owner, true, count)).Id);
        await Error(409, () => d.Corrections.StockAsync(d.Business, d.Product, d.Owner, true, count with { IdempotencyKey = "stale-count-001" }));
        await Error(409, () => d.Corrections.StockAsync(d.Business, d.Product, d.Owner, false, new(-18, 17, Today, "Invalid loss quantity", "negative-loss-001")));
        await d.Corrections.StockAsync(d.Business, d.Product, d.Owner, false, new(-2, 17, Today, "Goods damaged on shelf", "damaged-stock-001"));
        Assert.Equal(15, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
        var staff = await Member(scope.ServiceProvider, d, BusinessRole.Staff);
        await Error(403, () => d.Corrections.StockAsync(d.Business, d.Product, staff, true, new(15, 15, Today, "Staff cannot adjust", "staff-count-001")));
        _ = await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner);
        var saved = await d.Db.InventoryCorrections.FirstAsync(); saved.Reason = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => d.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Purchase_reversal_cannot_remove_stock_already_consumed_and_debt_correction_preserves_history()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var purchase = await d.Sales.RecordAsync(d.Business, d.Owner, "Purchase", new(Today, [new(d.Product, d.Unit, 5, 20)], "purchase-reverse-001"));
        await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, 24, 1, 24));
        await Error(409, () => d.Corrections.ReturnAsync(d.Business, purchase.Transaction.Id, d.Owner, new(Today, "Reverse consumed purchase", "purchase-return-001", ReverseRemaining: true)));
        var opening = await d.Ledger.OpeningAsync(d.Business, (await d.Ledger.CreateAsync(d.Business, d.Owner, new("OTHER", "Other Customer"))).Id, d.Owner,
            new(100, Today, "Existing customer balance", "other-opening-001"));
        await d.Corrections.OpeningDebtAsync(d.Business, opening.Id, d.Owner, new(80, Today, "Correct onboarding transcription", "opening-correction-001"));
        Assert.Equal(10000, (await d.Db.CustomerCharges.AsNoTracking().SingleAsync(c => c.Id == opening.Id)).AmountMinor);
        Assert.Equal(-2000, (await d.Db.CustomerChargeAdjustments.SingleAsync()).DeltaMinor);
    }

    [Fact]
    public async Task Daily_notifications_obey_local_time_dedupe_and_retry_failed_enqueue()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var settings = await d.Db.Businesses.SingleAsync(); settings.DailyNotificationTime = new TimeOnly(9, 0); await d.Db.SaveChangesAsync();
        var fake = new MutableClock(new DateTimeOffset(2026, 10, 26, 7, 59, 0, TimeSpan.Zero));
        var sender = new TestAccountEmailSender(); var worker = new LowStockNotificationProcessor(d.Db, sender, fake);
        // Lagos is UTC+1: 07:59 UTC is before the configured 09:00 send time.
        Assert.False(await worker.QueueAsync(d.Business)); Assert.Empty(sender.SentMessages);
        fake.Now = fake.Now.AddMinutes(1);
        // No low stock still records today's scan; newly low stock waits until tomorrow.
        Assert.False(await worker.QueueAsync(d.Business));
        await d.Corrections.StockAsync(d.Business, d.Product, d.Owner, true, new(0, 20, Today, "Count empty shelves", "notification-count"));
        Assert.False(await worker.QueueAsync(d.Business));
        fake.Now = fake.Now.AddDays(1);
        var failing = new LowStockNotificationProcessor(d.Db, new FailingSender(), fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.QueueAsync(d.Business));
        Assert.True(await worker.QueueAsync(d.Business)); Assert.False(await worker.QueueAsync(d.Business));
        Assert.Single(sender.SentMessages); Assert.Contains("COMPLETE", sender.SentMessages.Single().Body);
        Assert.Equal(2, await d.Db.BusinessNotificationDispatches.CountAsync());
    }

    [Fact]
    public async Task Excel_exports_store_untrusted_text_as_text_and_financial_reports_deny_staff_and_foreign_tenants()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var product = await d.Db.InventoryProducts.SingleAsync(); product.Name = "=HYPERLINK(\"https://example.test\")"; await d.Db.SaveChangesAsync();
        var table = await d.Reports.TableAsync(d.Business, d.Owner, "inventory", null, null, export: true);
        using var zip = new ZipArchive(new MemoryStream(InventoryWorkbook.Create(table)));
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var sheet = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Empty(sheet.Descendants(ns + "f"));
        Assert.Contains(sheet.Descendants(ns + "c"), c => c.Attribute("t")?.Value == "inlineStr" && c.Value.Contains("=HYPERLINK"));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        var staff = await Member(scope.ServiceProvider, d, BusinessRole.Staff);
        await Error(403, () => d.Reports.TableAsync(d.Business, staff, "customers", null, null));
        await Error(404, () => d.Reports.TableAsync(Guid.NewGuid(), d.Owner, "inventory", null, null));
        await Error(400, () => d.Reports.TableAsync(d.Business, d.Owner, "inventory", null, null, page: 1001));
    }

    [Fact]
    public async Task Ownership_transfer_closure_and_reopening_keep_one_owner_and_archive_export_access()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var target = await Member(scope.ServiceProvider, d, BusinessRole.Staff);
        await d.Lifecycle.TransferAsync(d.Business, d.Owner, target);
        Assert.Equal(target, (await d.Db.BusinessMemberships.AsNoTracking().SingleAsync(m => m.BusinessId == d.Business && m.Role == BusinessRole.Owner)).UserId);
        await Error(404, () => d.Lifecycle.CloseAsync(d.Business, d.Owner, "Completion Store", false));
        await d.Lifecycle.CloseAsync(d.Business, target, "Completion Store", false);
        await Error(404, () => d.Inventory.GetProductAsync(d.Business, d.Product, target));
        await Error(404, () => d.Reports.TableAsync(d.Business, d.Owner, "inventory", null, null));
        Assert.Single((await d.Reports.TableAsync(d.Business, target, "inventory", null, null)).Rows);
        await d.Lifecycle.CloseAsync(d.Business, target, "Completion Store", true);
        Assert.Equal(20, (await d.Inventory.GetProductAsync(d.Business, d.Product, target)).QuantityOnHand);
        Assert.False((await d.Db.Businesses.AsNoTracking().SingleAsync()).LowStockEmailsEnabled);
    }

    [Fact]
    public async Task Cancelled_invitations_cannot_be_used_and_closed_accounts_cannot_reset_their_way_back_in()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<BusinessService>().InviteAsync(d.Business, d.Owner, new("invited@example.test", "Staff"));
        var invite = await d.Db.BusinessInvitations.AsNoTracking().SingleAsync();
        await d.Lifecycle.CancelAsync(d.Business, invite.Id, d.Owner);
        Assert.NotNull((await d.Db.BusinessInvitations.AsNoTracking().SingleAsync()).RevokedAtUtc);
        var member = await Member(scope.ServiceProvider, d, BusinessRole.Staff);
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>(); var user = (await manager.FindByIdAsync(member.ToString()))!;
        var reset = await manager.GeneratePasswordResetTokenAsync(user);
        var encodedReset = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(reset));
        await d.Lifecycle.CloseAccountAsync(member);
        Assert.NotNull((await d.Db.Users.AsNoTracking().SingleAsync(u => u.Id == member)).AccountClosedAtUtc);
        Assert.False((await d.Db.BusinessMemberships.AsNoTracking().SingleAsync(m => m.UserId == member)).IsActive);
        Assert.False(await scope.ServiceProvider.GetRequiredService<AuthService>().ResetPasswordAsync(user.Email, encodedReset, "Another-Cedar-River-593!"));
        using var client = f.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = user.UserName, password = "Cedar-Moon-River-593!" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        await Error(409, () => d.Lifecycle.CloseAccountAsync(d.Owner));
    }

    [Fact]
    public async Task Retired_catalog_and_global_user_routes_are_absent()
    {
        using var root = new TradeNestWebApplicationFactory();
        using var f = root.WithWebHostBuilder(b => b.UseSetting("Legacy:EnableTradeNestEndpoints", "true").UseSetting("Legacy:EnablePlatformUserAdministration", "true"));
        using var client = f.CreateClient();
        foreach (var path in new[] { "/games", "/genres", "/auth/users" })
        { using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); }
    }

    [Fact]
    public async Task Concurrent_return_requests_cannot_return_the_same_units_twice_and_exports_are_private()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var sale = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, 2, 10, 20));
        using var client = f.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = "Bootstrap-Cedar-River-593!" });
        login.EnsureSuccessStatusCode(); var token = (await login.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        var path = $"/businesses/{d.Business}/transactions/{sale.Transaction.Id}/returns";
        var request = new ReturnInventoryDto(Today, "Concurrent complete return", "concurrent-return-a", ReverseRemaining: true, RefundAmount: 20, RefundMethod: "Cash");
        var results = await Task.WhenAll(client.PostAsJsonAsync(path, request), client.PostAsJsonAsync(path, request with { IdempotencyKey = "concurrent-return-b" }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in results) response.Dispose();
        Assert.Equal(20, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
        using var export = await client.GetAsync($"/businesses/{d.Business}/reports/exports/inventory.xlsx");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode); Assert.True(export.Headers.CacheControl!.NoStore);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType!.MediaType);
        var correction = await d.Db.InventoryCorrections.AsNoTracking().SingleAsync();
        using var details = await client.GetAsync($"/businesses/{d.Business}/corrections/{correction.Id}");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        using var audit = await client.GetAsync($"/businesses/{d.Business}/audit-events");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
    }

    [Fact]
    public async Task Production_outbox_and_dispatch_commit_together_and_excel_rejects_unbounded_downloads()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var business = await d.Db.Businesses.SingleAsync(); business.DailyNotificationTime = TimeOnly.MinValue; await d.Db.SaveChangesAsync();
        await d.Corrections.StockAsync(d.Business, d.Product, d.Owner, true, new(0, 20, Today, "Count stock out", "outbox-count-001"));
        var sender = new OutboxAccountEmailSender(d.Db, scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
            TimeProvider.System, Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Identity.DataProtectionTokenProviderOptions()));
        var worker = new LowStockNotificationProcessor(d.Db, sender, TimeProvider.System);
        await d.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailDispatch BEFORE INSERT ON BusinessNotificationDispatches BEGIN SELECT RAISE(ABORT, 'failure'); END");
        await Assert.ThrowsAsync<DbUpdateException>(() => worker.QueueAsync(d.Business));
        Assert.Empty(await d.Db.EmailOutbox.ToListAsync()); Assert.Empty(await d.Db.BusinessNotificationDispatches.ToListAsync());
        await d.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailDispatch");
        Assert.True(await worker.QueueAsync(d.Business)); Assert.False(await worker.QueueAsync(d.Business));
        Assert.Single(await d.Db.EmailOutbox.ToListAsync());
        // Generate fixture rows inside this tenant; 10,001 rows must fail rather
        // than silently returning a truncated spreadsheet presented as complete.
        var stockUnitId = (await d.Db.InventoryProducts.AsNoTracking().SingleAsync(p => p.Id == d.Product)).StockUnitId;
        var products = Enumerable.Range(1, 10000).Select(i => new InventoryProduct { BusinessId = d.Business, Name = "Export product", Sku = "LIMIT-" + i,
            NormalizedSku = "LIMIT-" + i, StockUnit = "Each", StockUnitId = stockUnitId, CreatedAtUtc = DateTime.UtcNow });
        d.Db.InventoryProducts.AddRange(products); await d.Db.SaveChangesAsync();
        await Error(413, () => d.Reports.TableAsync(d.Business, d.Owner, "inventory", null, null, export: true));
        Assert.Equal(100, (await d.Reports.TableAsync(d.Business, d.Owner, "inventory", null, null)).Rows.Count);
    }

    private sealed record Data(TradeNestContext Db, InventoryService Inventory, InventoryTransactionService Sales,
        CustomerLedgerService Ledger, InventoryCorrectionService Corrections, InventoryReportService Reports,
        BusinessLifecycleService Lifecycle, Guid Business, int Owner, Guid Product, Guid Unit, Guid Customer);
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    private static async Task<Data> Setup(IServiceProvider services)
    {
        var db = services.GetRequiredService<TradeNestContext>(); var owner = (await db.Users.SingleAsync()).Id;
        var business = await services.GetRequiredService<BusinessService>().CreateAsync(owner, new("Completion Store", "NGN", "stock@example.test", "Africa/Lagos"));
        var inventory = services.GetRequiredService<InventoryService>();
        var product = await inventory.CreateProductAsync(business.Id, owner, new("Completion product", "COMPLETE", "Each", false));
        var unit = (await inventory.UnitsAsync(business.Id, product.Id, owner, 1, 20)).Items.Single();
        await inventory.OpeningStockAsync(business.Id, product.Id, owner, new(unit.Id, 20, Today.AddDays(-2), "Opening stock count", "complete-opening-001"));
        var ledger = services.GetRequiredService<CustomerLedgerService>(); var customer = await ledger.CreateAsync(business.Id, owner, new("MAIN", "Main Customer"));
        return new(db, inventory, services.GetRequiredService<InventoryTransactionService>(), ledger,
            services.GetRequiredService<InventoryCorrectionService>(), services.GetRequiredService<InventoryReportService>(),
            services.GetRequiredService<BusinessLifecycleService>(), business.Id, owner, product.Id, unit.Id, customer.Id);
    }
    private static RecordInventoryTransactionDto Sale(Data d, decimal quantity, decimal price, decimal paid) => new(Today,
        [new(d.Product, d.Unit, quantity, price)], "completion-sale-001", CustomerId: d.Customer, AmountPaid: paid, PaymentMethod: paid > 0 ? "Cash" : null);
    private static async Task<int> Member(IServiceProvider services, Data d, BusinessRole role)
    {
        var user = new User { UserName = "member-" + Guid.NewGuid().ToString("N")[..8], Email = Guid.NewGuid().ToString("N") + "@example.test", EmailConfirmed = true};
        Assert.True((await services.GetRequiredService<UserManager<User>>().CreateAsync(user, "Cedar-Moon-River-593!")).Succeeded);
        d.Db.BusinessMemberships.Add(new() { BusinessId = d.Business, UserId = user.Id, Role = role, JoinedAtUtc = DateTime.UtcNow }); await d.Db.SaveChangesAsync(); return user.Id;
    }
    private static async Task Error(int code, Func<Task> work) => Assert.Equal(code, (await Assert.ThrowsAsync<BusinessRequestException>(work)).StatusCode);
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class FailingSender : IAccountEmailSender
    { public Task SendAsync(string emailAddress, string subject, string textBody, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Outbox unavailable"); }
}
