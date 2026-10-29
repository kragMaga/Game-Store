using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class BusinessInsightsTests
{
    [Fact]
    public async Task Product_customer_and_opening_imports_preview_without_changes_then_commit_once()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var preview = await d.Imports.PreviewAsync(d.Business, d.Owner, "products", Workbook("products", ["NEW", "Imported item", "Each", false, 2, 12.5m, 10, Today, "Opening goods"]));
        Assert.Empty(preview.Errors); Assert.NotNull(preview.PreviewToken);
        Assert.Equal(1, await d.Db.InventoryProducts.CountAsync());
        var committed = Json(await d.Imports.CommitAsync(d.Business, d.Owner, preview.PreviewToken!));
        Assert.False(committed.GetProperty("AlreadyCommitted").GetBoolean());
        Assert.True(Json(await d.Imports.CommitAsync(d.Business, d.Owner, preview.PreviewToken!)).GetProperty("AlreadyCommitted").GetBoolean());
        var product = await d.Db.InventoryProducts.AsNoTracking().SingleAsync(p => p.Sku == "NEW");
        Assert.Equal(10_000_000, product.QuantityOnHandMicros); Assert.Equal(12_500_000, product.DefaultSellingPriceMicros);
        var customer = await d.Imports.PreviewAsync(d.Business, d.Owner, "customers", Workbook("customers", ["CUST", "Imported customer", "customer@example.test", "", 125, Today.AddDays(-2), Today.AddDays(-1), "Existing debt"]));
        Assert.Empty(customer.Errors); await d.Imports.CommitAsync(d.Business, d.Owner, customer.PreviewToken!);
        var imported = await d.Db.Customers.AsNoTracking().SingleAsync();
        Assert.Equal(125, (await d.Ledger.StatementAsync(d.Business, imported.Id, d.Owner, 1, 20)).Balance.Outstanding);
        var opening = await d.Imports.PreviewAsync(d.Business, d.Owner, "opening-stock", Workbook("opening-stock", ["EXIST", 5, "Each", Today, "Existing stock"]));
        Assert.Empty(opening.Errors); await d.Imports.CommitAsync(d.Business, d.Owner, opening.PreviewToken!);
        Assert.Equal(5, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
        Assert.Equal(3, await d.Db.InventoryImportBatches.CountAsync());
    }

    [Fact]
    public async Task Import_errors_stale_previews_and_foreign_reviewers_cannot_commit_partial_changes()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var bad = await d.Imports.PreviewAsync(d.Business, d.Owner, "products", Workbook("products",
            ["VALID", "Valid product", "Each", false, 0, "", 1, Today, "Opening goods"], ["BAD", "Bad quantity", "Each", false, 0, "", 0.5m, Today, "Opening goods"]));
        Assert.Single(bad.Errors); Assert.Equal(3, bad.Errors[0].Row); Assert.Null(bad.PreviewToken);
        Assert.False(await d.Db.InventoryProducts.AnyAsync(p => p.Sku == "VALID"));
        var good = await d.Imports.PreviewAsync(d.Business, d.Owner, "products", Workbook("products", ["LATE", "Later product", "Each", false, 0, "", "", "", ""]));
        var other = await d.Businesses.CreateAsync(d.Owner, new("Other Store", "NGN", "other@example.test", "Africa/Lagos"));
        await Error(400, () => d.Imports.CommitAsync(other.Id, d.Owner, good.PreviewToken!));
        await d.Inventory.CreateProductAsync(d.Business, d.Owner, new("Concurrent new product", "LATE", "Each", false));
        await Error(409, () => d.Imports.CommitAsync(d.Business, d.Owner, good.PreviewToken!));
        _ = await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner);
        Assert.Empty(await d.Db.InventoryImportBatches.ToListAsync());
        var admin = await Member(scope.ServiceProvider, d, BusinessRole.Admin);
        await Error(400, () => d.Imports.CommitAsync(d.Business, admin, good.PreviewToken!));
        var staff = await Member(scope.ServiceProvider, d, BusinessRole.Staff);
        await Error(403, () => d.Imports.PreviewAsync(d.Business, staff, "products", Workbook("products", ["STAFF", "No authority", "Each", false, 0, "", "", "", ""])));
    }

    [Fact]
    public async Task Spreadsheet_formulas_and_extra_rows_are_rejected_and_http_preview_is_bounded_and_private()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var file = Workbook("products", ["HTTP", "HTTP product", "Each", false, 0, "", "", "", ""]);
        using var memory = new MemoryStream(); memory.Write(file);
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!; XDocument xml; using (var input = entry.Open()) xml = XDocument.Load(input);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            xml.Descendants(ns + "c").First().Add(new XElement(ns + "f", "1+1")); entry.Delete(); using var output = zip.CreateEntry("xl/worksheets/sheet1.xml").Open(); xml.Save(output);
        }
        await Error(400, () => d.Imports.PreviewAsync(d.Business, d.Owner, "products", memory.ToArray()));
        var excessive = Enumerable.Range(1, 1001).Select(i => new object?[] { "ROW-" + i, "Product", "Each", false, 0, "", "", "", "" }).ToArray();
        Assert.Throws<BusinessRequestException>(() => InventorySpreadsheet.Read(Workbook("products", excessive)));
        using var client = await Login(f);
        using var content = new ByteArrayContent(file); content.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var response = await client.PostAsync($"/businesses/{d.Business}/imports/products/preview", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var preview = (await response.Content.ReadFromJsonAsync<ImportPreviewDto>())!; Assert.NotNull(preview.PreviewToken);
        using var commit = await client.PostAsJsonAsync($"/businesses/{d.Business}/imports/commit", new CommitInventoryImportDto(preview.PreviewToken!));
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
    }

    [Fact]
    public async Task Reconciliation_includes_openings_receipts_sales_returns_and_counts_in_base_units()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        await d.Inventory.OpeningStockAsync(d.Business, d.Product, d.Owner, new(d.Unit, 20, Today.AddDays(-5), "Opening count", "recon-opening-001"));
        await d.Sales.RecordAsync(d.Business, d.Owner, "Purchase", new(Today.AddDays(-2), [new(d.Product, d.Unit, 5, 10)], "recon-purchase-001"));
        var sale = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", new(Today.AddDays(-1), [new(d.Product, d.Unit, 4, 10)], "recon-sale-001", AmountPaid: 40, PaymentMethod: "Cash"));
        await d.Corrections.ReturnAsync(d.Business, sale.Transaction.Id, d.Owner, new(Today, "Returned one item", "recon-return-001", [new(sale.Lines.Single().Id, 1)], RefundAmount: 10, RefundMethod: "Cash"));
        await d.Corrections.StockAsync(d.Business, d.Product, d.Owner, true, new(20, 22, Today, "Physical stock count", "recon-count-001"));
        var report = await d.Insights.ReconciliationAsync(d.Business, d.Owner, Today.AddDays(-2), Today, 1, 20);
        var row = Assert.Single(report.Rows);
        Assert.Equal(20m, row[3]); Assert.Equal(5m, row[5]); Assert.Equal(-4m, row[6]); Assert.Equal(1m, row[7]); Assert.Equal(-2m, row[9]); Assert.Equal(20m, row[10]);
        var viewer = await Member(scope.ServiceProvider, d, BusinessRole.Viewer);
        Assert.Single((await d.Insights.ReconciliationAsync(d.Business, viewer, Today.AddDays(-2), Today, 1, 20)).Rows);
        await Error(403, () => d.Insights.DashboardAsync(d.Business, viewer));
    }

    [Fact]
    public async Task Ageing_boundaries_use_outstanding_debt_and_dashboard_reminders_use_live_balances()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var offsets = new int?[] { null, 0, -1, -30, -31, -60, -61, -90, -91 };
        Guid payCustomer = default, payCharge = default;
        for (var i = 0; i < offsets.Length; i++)
        {
            var customer = await d.Ledger.CreateAsync(d.Business, d.Owner, new("AGE-" + i, "Customer " + i));
            var charge = await d.Ledger.OpeningAsync(d.Business, customer.Id, d.Owner, new(10, Today.AddDays(-200), "Imported old balance", "age-opening-" + i, offsets[i].HasValue ? Today.AddDays(offsets[i]!.Value) : null));
            if (i == 4) { payCustomer = customer.Id; payCharge = charge.Id; }
        }
        await d.Ledger.PayAsync(d.Business, payCustomer, d.Owner, new(5, Today, "Cash", [new(payCharge, 5)], "age-receipt-001"));
        var report = Json(await d.Insights.AgeingAsync(d.Business, d.Owner, 1, 2)); var totals = report.GetProperty("Totals");
        Assert.Equal(10, totals.GetProperty("Unscheduled").GetDecimal()); Assert.Equal(10, totals.GetProperty("Current").GetDecimal());
        Assert.Equal(20, totals.GetProperty("Days1To30").GetDecimal()); Assert.Equal(15, totals.GetProperty("Days31To60").GetDecimal());
        Assert.Equal(20, totals.GetProperty("Days61To90").GetDecimal()); Assert.Equal(10, totals.GetProperty("Over90").GetDecimal()); Assert.Equal(85, totals.GetProperty("Total").GetDecimal());
        Assert.Equal(9, (await d.Insights.AgeingExportAsync(d.Business, d.Owner)).Rows.Count);
        Assert.Equal(9, report.GetProperty("TotalCount").GetInt32()); Assert.Equal(2, report.GetProperty("Items").GetArrayLength());
        var dashboard = Json(await d.Insights.DashboardAsync(d.Business, d.Owner));
        Assert.Equal(65, dashboard.GetProperty("DebtReminder").GetProperty("Amount").GetDecimal());
        var staff = await Member(scope.ServiceProvider, d, BusinessRole.Staff); await Error(403, () => d.Insights.AgeingAsync(d.Business, staff, 1, 20));
    }

    [Fact]
    public async Task Reminder_emails_target_only_configured_addresses_and_dashboard_is_admin_only()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var admin = await Member(scope.ServiceProvider, d, BusinessRole.Admin); _ = await Member(scope.ServiceProvider, d, BusinessRole.Staff);
        var customer = await d.Ledger.CreateAsync(d.Business, d.Owner, new("DEBT", "Debtor", "never-contact@example.test"));
        await d.Ledger.OpeningAsync(d.Business, customer.Id, d.Owner, new(100, Today.AddDays(-2), "Existing overdue debt", "reminder-opening-001", Today.AddDays(-1)));
        var settings = d.Provider.GetRequiredService<BusinessNotificationService>();
        await settings.SettingsAsync(d.Business, d.Owner, new(true, ["finance@example.test", "FINANCE@example.test"]));
        var b = await d.Db.Businesses.SingleAsync(); b.DailyNotificationTime = TimeOnly.MinValue; await d.Db.SaveChangesAsync();
        var worker = d.Provider.GetRequiredService<DebtReminderProcessor>(); Assert.True(await worker.QueueAsync(d.Business)); Assert.False(await worker.QueueAsync(d.Business));
        var notifications = await d.Db.BusinessNotifications.AsNoTracking().ToListAsync(); Assert.Single(notifications);
        Assert.DoesNotContain(notifications, n => n.Recipient == "never-contact@example.test"); Assert.Contains(notifications, n => n.Recipient == "finance@example.test");
        Assert.All(notifications, n => Assert.Equal("OverdueDebt", n.Kind));
        Assert.Single(await d.Db.EmailOutbox.ToListAsync());
        var other = await d.Businesses.CreateAsync(d.Owner, new("Foreign business", "NGN", "foreign@example.test", "Africa/Lagos"));
        await Error(404, () => settings.RetryAsync(other.Id, notifications[0].Id, d.Owner));
        Assert.Equal(100, Json(await d.Insights.DashboardAsync(d.Business, admin)).GetProperty("DebtReminder").GetProperty("Amount").GetDecimal());
    }

    [Fact]
    public async Task Low_stock_delivery_history_tracks_expiry_retry_and_success_without_exposing_auth_mail()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var b = await d.Db.Businesses.SingleAsync(); b.DailyNotificationTime = TimeOnly.MinValue; await d.Db.SaveChangesAsync();
        var worker = d.Provider.GetRequiredService<LowStockNotificationProcessor>(); Assert.True(await worker.QueueAsync(d.Business));
        var notification = await d.Db.BusinessNotifications.AsNoTracking().SingleAsync(); Assert.Equal("LowStock", notification.Kind);
        await d.Db.EmailOutbox.Where(o => o.Id == notification.OutboxMessageId).ExecuteUpdateAsync(set => set.SetProperty(o => o.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        var transport = new RecordingTransport();
        var processor = new EmailOutboxProcessor(d.Db, transport, d.Provider.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System, d.Provider.GetRequiredService<ILogger<EmailOutboxProcessor>>());
        Assert.False(await processor.ProcessNextAsync(default));
        Assert.Equal("Expired", (await d.Db.BusinessNotifications.AsNoTracking().SingleAsync()).Status);
        var service = d.Provider.GetRequiredService<BusinessNotificationService>(); await service.RetryAsync(d.Business, notification.Id, d.Owner);
        Assert.True(await processor.ProcessNextAsync(default));
        var saved = await d.Db.BusinessNotifications.AsNoTracking().SingleAsync(); Assert.Equal("Sent", saved.Status); Assert.Equal(1, saved.Attempts); Assert.Equal(1, transport.Count);
        Assert.Equal(1, Json(await service.HistoryAsync(d.Business, d.Owner, 1, 20)).GetProperty("TotalCount").GetInt32());
        await Error(409, () => service.RetryAsync(d.Business, saved.Id, d.Owner));
    }

    [Fact]
    public async Task Suggested_prices_are_editable_without_repricing_posted_sales_or_forcing_the_default()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        await d.Inventory.UpdateProductAsync(d.Business, d.Product, d.Owner, new("Existing product", 0, true, DefaultSellingPrice: 25));
        Assert.Equal(25, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).DefaultSellingPrice);
        await d.Inventory.OpeningStockAsync(d.Business, d.Product, d.Owner, new(d.Unit, 2, Today, "Opening goods", "price-opening-001"));
        var sale = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", new(Today, [new(d.Product, d.Unit, 1, 20)], "price-sale-001", AmountPaid: 20, PaymentMethod: "Cash"));
        await d.Inventory.UpdateProductAsync(d.Business, d.Product, d.Owner, new("Existing product", 0, true, DefaultSellingPrice: 30));
        Assert.Equal(20, (await d.Sales.GetAsync(d.Business, sale.Transaction.Id, d.Owner, "Sale")).Transaction.Total);
        await Error(400, () => d.Inventory.UpdateProductAsync(d.Business, d.Product, d.Owner, new("Existing product", 0, true, DefaultSellingPrice: -1)));
    }

    [Fact]
    public async Task Queued_reminders_are_cancelled_if_the_business_removes_the_recipient_before_delivery()
    {
        using var f = new TradeNestWebApplicationFactory(); using var scope = f.Services.CreateScope(); var d = await Setup(scope.ServiceProvider);
        var admin = await Member(scope.ServiceProvider, d, BusinessRole.Admin);
        var email = (await d.Db.Users.AsNoTracking().SingleAsync(u => u.Id == admin)).Email!;
        var customer = await d.Ledger.CreateAsync(d.Business, d.Owner, new("OVERDUE", "Overdue customer"));
        await d.Ledger.OpeningAsync(d.Business, customer.Id, d.Owner, new(10, Today.AddDays(-2), "Existing debt", "revoked-reminder-001", Today.AddDays(-1)));
        var ownerEmail = (await d.Db.Users.AsNoTracking().SingleAsync(u => u.Id == d.Owner)).Email!;
        var service = d.Provider.GetRequiredService<BusinessNotificationService>(); await service.SettingsAsync(d.Business, d.Owner, new(true, [ownerEmail, email]));
        var b = await d.Db.Businesses.SingleAsync(); b.DailyNotificationTime = TimeOnly.MinValue; await d.Db.SaveChangesAsync();
        await d.Provider.GetRequiredService<DebtReminderProcessor>().QueueAsync(d.Business);
        await service.SettingsAsync(d.Business, d.Owner, new(true, [ownerEmail]));
        var transport = new RecordingTransport();
        var processor = new EmailOutboxProcessor(d.Db, transport, d.Provider.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System, d.Provider.GetRequiredService<ILogger<EmailOutboxProcessor>>());
        while (await processor.ProcessNextAsync(default)) { }
        Assert.Equal(1, transport.Count); // Only the remaining specified recipient receives the email.
        var cancelled = await d.Db.BusinessNotifications.AsNoTracking().SingleAsync(n => n.Recipient == email.ToLowerInvariant());
        Assert.Equal("Cancelled", cancelled.Status); Assert.Equal("", cancelled.ProtectedPayload);
    }

    private sealed record Data(IServiceProvider Provider, TradeNestContext Db, InventoryService Inventory, InventoryTransactionService Sales,
        CustomerLedgerService Ledger, InventoryCorrectionService Corrections, BusinessService Businesses, BusinessInsightService Insights, InventoryImportService Imports,
        Guid Business, int Owner, Guid Product, Guid Unit);
    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Africa/Lagos").DateTime);
    private static async Task<Data> Setup(IServiceProvider provider)
    {
        var db = provider.GetRequiredService<TradeNestContext>(); var owner = (await db.Users.SingleAsync()).Id; var businesses = provider.GetRequiredService<BusinessService>();
        var b = await businesses.CreateAsync(owner, new("Insight Store", "NGN", "stock@example.test", "Africa/Lagos"));
        var inventory = provider.GetRequiredService<InventoryService>(); var product = await inventory.CreateProductAsync(b.Id, owner, new("Existing product", "EXIST", "Each", false));
        var unit = (await inventory.UnitsAsync(b.Id, product.Id, owner, 1, 20)).Items.Single();
        return new(provider, db, inventory, provider.GetRequiredService<InventoryTransactionService>(), provider.GetRequiredService<CustomerLedgerService>(),
            provider.GetRequiredService<InventoryCorrectionService>(), businesses, provider.GetRequiredService<BusinessInsightService>(), provider.GetRequiredService<InventoryImportService>(), b.Id, owner, product.Id, unit.Id);
    }
    private static byte[] Workbook(string kind, params object?[][] rows) => InventoryWorkbook.Create(new(kind, "NGN", InventoryImportService.Columns(kind), rows, rows.Length, 1, 100));
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static async Task Error(int code, Func<Task> work) => Assert.Equal(code, (await Assert.ThrowsAsync<BusinessRequestException>(work)).StatusCode);
    private static async Task<int> Member(IServiceProvider provider, Data d, BusinessRole role)
    {
        var user = new User { UserName = "member-" + Guid.NewGuid().ToString("N")[..8], Email = Guid.NewGuid().ToString("N") + "@example.test", EmailConfirmed = true};
        Assert.True((await provider.GetRequiredService<UserManager<User>>().CreateAsync(user, "Cedar-Moon-River-593!")).Succeeded);
        d.Db.BusinessMemberships.Add(new() { BusinessId = d.Business, UserId = user.Id, Role = role, JoinedAtUtc = DateTime.UtcNow }); await d.Db.SaveChangesAsync(); return user.Id;
    }
    private static async Task<HttpClient> Login(TradeNestWebApplicationFactory f)
    {
        var client = f.CreateClient(); using var response = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = "Bootstrap-Cedar-River-593!" });
        response.EnsureSuccessStatusCode(); client.DefaultRequestHeaders.Authorization = new("Bearer", (await response.Content.ReadFromJsonAsync<AuthTokensDto>())!.AccessToken); return client;
    }
    private sealed class RecordingTransport : IEmailTransport
    { public int Count { get; private set; } public Task SendAsync(string emailAddress, string subject, string textBody, CancellationToken cancellationToken = default) { Count++; return Task.CompletedTask; } }
}
