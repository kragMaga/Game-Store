using System.Net;
using System.Net.Http.Json;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class InventoryTransactionTests
{
    [Fact]
    public async Task Receipts_and_variable_price_sales_preserve_snapshots_units_totals_and_posting_order()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var data = await Setup(scope.ServiceProvider);
        var supplier = await data.Transactions.CreateSupplierAsync(data.Business, data.Owner, new("Main Supplier", "Contact on file"));
        var crate = await data.Inventory.CreateUnitAsync(data.Business, data.Product, data.Owner, new("Crate", 24));
        var purchase = await data.Transactions.RecordAsync(data.Business, data.Owner, "Purchase",
            PurchaseRequest("purchase-001", new InventoryTransactionLineRequest(data.Product, crate.Id, 2, 10000)) with { SupplierId = supplier.Id });
        Assert.Equal(20000, purchase.Transaction.Total);
        Assert.Equal(48, Assert.Single(purchase.Lines).BaseQuantity);
        var request = Request("sale-variable-001", new InventoryTransactionLineRequest(data.Product, data.Unit, 3, 500), new InventoryTransactionLineRequest(data.Product, data.Unit, 2, 450));
        var sale = await data.Transactions.RecordAsync(data.Business, data.Owner, "Sale", request);
        Assert.Equal(2400, sale.Transaction.Total);
        Assert.Equal(53, (await data.Inventory.GetProductAsync(data.Business, data.Product, data.Owner)).QuantityOnHand);
        Assert.Equal(sale.Transaction.Id, (await data.Transactions.RecordAsync(data.Business, data.Owner, "Sale", request)).Transaction.Id);
        Assert.Equal(2, await data.Db.InventoryTransactions.CountAsync());
        await data.Inventory.UpdateProductAsync(data.Business, data.Product, data.Owner, new("Renamed Product", 0, true));
        var original = await data.Transactions.GetAsync(data.Business, sale.Transaction.Id, data.Owner, "Sale");
        Assert.All(original.Lines, line => Assert.Equal("Bottled Water", line.ProductName));
        Assert.Equal(new[] { 500m, 450m }, original.Lines.Select(line => line.UnitPrice));
        var history = (await data.Inventory.MovementsAsync(data.Business, data.Product, data.Owner, 1, 20)).Items;
        Assert.Equal(new[] { 4L, 3L, 2L, 1L }, history.Select(item => item.SequenceNumber));
        Assert.Equal(new[] { 53m, 55m, 58m, 10m }, history.Select(item => item.BalanceAfter));
        Assert.Equal(-2, history[0].BaseQuantity);
        var changed = await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.RecordAsync(data.Business, data.Owner, "Sale",
            request with { Lines = new[] { new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 600) } }));
        Assert.Equal(409, changed.StatusCode);
        var savedLine = await data.Db.InventoryTransactionLines.FirstAsync();
        savedLine.UnitPriceMicros = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Failed_later_lines_and_persistence_errors_leave_no_partial_sale_or_stock_changes()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var data = await Setup(scope.ServiceProvider);
        var failed = await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.RecordAsync(data.Business, data.Owner, "Sale",
            Request("too-large-001", new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 20), new InventoryTransactionLineRequest(data.Product, data.Unit, 10, 20))));
        Assert.Equal(409, failed.StatusCode);
        Assert.Equal(10, (await data.Inventory.GetProductAsync(data.Business, data.Product, data.Owner)).QuantityOnHand);
        Assert.Empty(await data.Db.InventoryTransactions.ToListAsync());
        await data.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailSaleLine BEFORE INSERT ON InventoryTransactionLines BEGIN SELECT RAISE(ABORT, 'Simulated line failure'); END");
        var request = Request("retry-after-failure", new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 20));
        await Assert.ThrowsAsync<DbUpdateException>(() => data.Transactions.RecordAsync(data.Business, data.Owner, "Sale", request));
        Assert.Equal(10, (await data.Inventory.GetProductAsync(data.Business, data.Product, data.Owner)).QuantityOnHand);
        Assert.Empty(await data.Db.InventoryTransactions.ToListAsync());
        Assert.Empty(await data.Db.InventoryTransactionLines.ToListAsync());
        Assert.Equal(1, await data.Db.StockMovements.CountAsync());
        Assert.False(await data.Db.BusinessAuditEvents.AnyAsync(item => item.BusinessId == data.Business && item.Action == "SaleRecorded"));
        await data.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailSaleLine");
        Assert.Equal(20, (await data.Transactions.RecordAsync(data.Business, data.Owner, "Sale", request)).Transaction.Total);
        Assert.Equal(9, (await data.Inventory.GetProductAsync(data.Business, data.Product, data.Owner)).QuantityOnHand);
    }

    [Theory]
    [InlineData(0, "1.5", "2")]
    [InlineData(2, "0.005", "0.01")]
    [InlineData(3, "1.2345", "1.235")]
    [InlineData(4, "1.23445", "1.2345")]
    public async Task Declared_currency_precision_is_preserved_including_zero_and_line_rounding_is_explicit(int places, string priceText, string totalText)
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var data = await Setup(scope.ServiceProvider, places);
        var price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);
        var total = decimal.Parse(totalText, System.Globalization.CultureInfo.InvariantCulture);
        var sale = await data.Transactions.RecordAsync(data.Business, data.Owner, "Sale", Request("rounding-001", new InventoryTransactionLineRequest(data.Product, data.Unit, 1, price)) with { AmountPaid = total });
        Assert.Equal(places, (await data.Db.Businesses.AsNoTracking().SingleAsync()).CurrencyDecimalPlaces);
        Assert.Equal(places, sale.Transaction.CurrencyDecimalPlaces);
        Assert.Equal(price, Assert.Single(sale.Lines).UnitPrice);
        Assert.Equal(total, sale.Transaction.Total);
    }

    [Fact]
    public async Task Tenant_boundaries_cover_products_units_suppliers_and_completed_documents()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var data = await Setup(scope.ServiceProvider);
        var otherBusiness = await scope.ServiceProvider.GetRequiredService<BusinessService>().CreateAsync(data.Owner,
            new("Other Business", "NGN", "other@example.test", "Africa/Lagos"));
        var otherProduct = await data.Inventory.CreateProductAsync(otherBusiness.Id, data.Owner, new("Other Product", "OTHER", "Each", false));
        var otherUnit = (await data.Inventory.UnitsAsync(otherBusiness.Id, otherProduct.Id, data.Owner, 1, 20)).Items.Single();
        var otherSupplier = await data.Transactions.CreateSupplierAsync(otherBusiness.Id, data.Owner, new("Foreign Supplier"));
        await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.RecordAsync(data.Business, data.Owner, "Purchase",
            PurchaseRequest("foreign-product", new InventoryTransactionLineRequest(otherProduct.Id, otherUnit.Id, 1, 1))));
        await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.RecordAsync(data.Business, data.Owner, "Purchase",
            PurchaseRequest("foreign-unit-001", new InventoryTransactionLineRequest(data.Product, otherUnit.Id, 1, 1))));
        await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.RecordAsync(data.Business, data.Owner, "Purchase",
            PurchaseRequest("foreign-supplier", new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 1)) with { SupplierId = otherSupplier.Id }));
        var purchase = await data.Transactions.RecordAsync(otherBusiness.Id, data.Owner, "Purchase", PurchaseRequest("valid-other-001", new InventoryTransactionLineRequest(otherProduct.Id, otherUnit.Id, 1, 1)));
        await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.GetAsync(data.Business, purchase.Transaction.Id, data.Owner, "Purchase"));
        Assert.Empty((await data.Transactions.ListAsync(data.Business, data.Owner, "Purchase", 1, 20)).Items);
    }

    [Fact]
    public async Task Staff_can_post_but_only_read_their_entries_and_viewers_cannot_post_or_read_financial_records()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var data = await Setup(scope.ServiceProvider);
        var staff = await AddMember(scope.ServiceProvider, data.Business, "sales-staff", BusinessRole.Staff);
        var second = await AddMember(scope.ServiceProvider, data.Business, "other-staff", BusinessRole.Staff);
        var viewer = await AddMember(scope.ServiceProvider, data.Business, "stock-viewer", BusinessRole.Viewer);
        using var client = await Login(factory, staff.UserName!, "Cedar-Moon-River-593!");
        using var response = await client.PostAsJsonAsync($"/businesses/{data.Business}/sales", Request("staff-sale-001", new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 50)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var sale = (await response.Content.ReadFromJsonAsync<InventoryTransactionDto>())!;
        Assert.Equal(staff.Id, sale.Transaction.ActorUserId);
        Assert.Equal(sale.Transaction.Id, (await data.Transactions.GetAsync(data.Business, sale.Transaction.Id, data.Owner, "Sale")).Transaction.Id);
        var inaccessible = await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.GetAsync(data.Business, sale.Transaction.Id, second.Id, "Sale"));
        Assert.Equal(404, inaccessible.StatusCode);
        await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.RecordAsync(data.Business, viewer.Id, "Sale", Request("viewer-sale-001", new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 1))));
        await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.ListAsync(data.Business, viewer.Id, "Sale", 1, 20));
        await data.Db.BusinessMemberships.Where(item => item.BusinessId == data.Business && item.UserId == staff.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsActive, false));
        using var revoked = await client.PostAsJsonAsync($"/businesses/{data.Business}/purchases", PurchaseRequest("revoked-purchase", new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 1)));
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [Fact]
    public async Task Concurrent_retries_add_stock_once_and_competing_sales_cannot_oversell()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var data = await Setup(scope.ServiceProvider);
        using var client = await Login(factory, "Admin", "Bootstrap-Cedar-River-593!");
        var root = $"/businesses/{data.Business}";
        var purchase = PurchaseRequest("concurrent-purchase", new InventoryTransactionLineRequest(data.Product, data.Unit, 2, 10));
        var retries = await Task.WhenAll(client.PostAsJsonAsync(root + "/purchases", purchase), client.PostAsJsonAsync(root + "/purchases", purchase));
        foreach (var response in retries) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); response.Dispose(); }
        var sales = await Task.WhenAll(client.PostAsJsonAsync(root + "/sales", Request("competing-sale-a", new InventoryTransactionLineRequest(data.Product, data.Unit, 8, 20))),
            client.PostAsJsonAsync(root + "/sales", Request("competing-sale-b", new InventoryTransactionLineRequest(data.Product, data.Unit, 8, 20))));
        Assert.Single(sales, item => item.StatusCode == HttpStatusCode.OK);
        Assert.Single(sales, item => item.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in sales) response.Dispose();
        Assert.Equal(4, (await data.Inventory.GetProductAsync(data.Business, data.Product, data.Owner)).QuantityOnHand);
        Assert.Equal(2, await data.Db.InventoryTransactions.CountAsync());
        Assert.Equal(3, await data.Db.StockMovements.CountAsync());
    }

    [Fact]
    public async Task Posting_limits_invalid_values_dates_and_large_lists_are_rejected_without_stock_effects()
    {
        using var root = new TradeNestWebApplicationFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimits:InventoryWritePermits"] = "1" })));
        using var scope = factory.Services.CreateScope();
        var data = await Setup(scope.ServiceProvider);
        foreach (var line in new[] { new InventoryTransactionLineRequest(data.Product, data.Unit, 0, 1),
                     new InventoryTransactionLineRequest(data.Product, data.Unit, 1, -1), new InventoryTransactionLineRequest(data.Product, data.Unit, 0.5m, 1), new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 0.0000001m) })
            await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.RecordAsync(data.Business, data.Owner, "Sale", Request("invalid-input-001", line)));
        await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.RecordAsync(data.Business, data.Owner, "Sale",
            Request("too-many-lines", Enumerable.Repeat(new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 1), 101).ToArray())));
        await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.RecordAsync(data.Business, data.Owner, "Sale",
            Request("old-date-001", new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 1)) with { EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10)) }));
        await Assert.ThrowsAsync<BusinessRequestException>(() => data.Transactions.RecordAsync(data.Business, data.Owner, "Sale",
            Request("future-date-001", new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 1)) with { EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)) }));
        using var client = await Login(factory, "Admin", "Bootstrap-Cedar-River-593!");
        using var first = await client.PostAsJsonAsync($"/businesses/{data.Business}/sales", Request("allowed-sale-001", new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 1)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var throttled = await client.PostAsJsonAsync($"/businesses/{data.Business}/purchases", PurchaseRequest("throttled-purchase", new InventoryTransactionLineRequest(data.Product, data.Unit, 1, 1)));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.InRange(throttled.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        using var oversized = await client.GetAsync($"/businesses/{data.Business}/sales?page=1000&pageSize=100");
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    [Fact]
    public async Task Migration_preserves_existing_openings_and_backfills_their_posting_sequence()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var data = await Setup(scope.ServiceProvider);
        var migrator = data.Db.GetService<IMigrator>();
        // Recreate the previous phase's schema with an actual opening balance,
        // then upgrade again. Empty-database migration tests would miss data loss.
        data.Db.ChangeTracker.Clear();
        await migrator.MigrateAsync("20261026001108_AddInventoryFoundation");
        await migrator.MigrateAsync();
        Assert.Equal(10, (await data.Inventory.GetProductAsync(data.Business, data.Product, data.Owner)).QuantityOnHand);
        Assert.Equal(1, Assert.Single((await data.Inventory.MovementsAsync(data.Business, data.Product, data.Owner, 1, 20)).Items).SequenceNumber);
        await data.Transactions.RecordAsync(data.Business, data.Owner, "Purchase",
            PurchaseRequest("post-migration-purchase", new InventoryTransactionLineRequest(data.Product, data.Unit, 2, 10)));
        Assert.Equal(12, (await data.Inventory.GetProductAsync(data.Business, data.Product, data.Owner)).QuantityOnHand);
        Assert.Equal(new[] { 2L, 1L }, (await data.Inventory.MovementsAsync(data.Business, data.Product, data.Owner, 1, 20)).Items.Select(item => item.SequenceNumber));
    }

    private sealed record TestData(TradeNestContext Db, InventoryService Inventory, InventoryTransactionService Transactions,
        Guid Business, int Owner, Guid Product, Guid Unit);
    private static async Task<TestData> Setup(IServiceProvider services, int places = 2)
    {
        var db = services.GetRequiredService<TradeNestContext>();
        var owner = (await db.Users.SingleAsync()).Id;
        var business = await services.GetRequiredService<BusinessService>().CreateAsync(owner,
            new("Transaction Store", "NGN", "stock@example.test", "Africa/Lagos", places));
        var inventory = services.GetRequiredService<InventoryService>();
        var product = await inventory.CreateProductAsync(business.Id, owner, new("Bottled Water", "WATER", "Bottle", false));
        var unit = (await inventory.UnitsAsync(business.Id, product.Id, owner, 1, 20)).Items.Single();
        await inventory.OpeningStockAsync(business.Id, product.Id, owner, new(unit.Id, 10,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)), "Opening stock before transactions", "transaction-opening"));
        return new(db, inventory, services.GetRequiredService<InventoryTransactionService>(), business.Id, owner, product.Id, unit.Id);
    }
    private static RecordInventoryTransactionDto Request(string key, params InventoryTransactionLineRequest[] lines) =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), lines, key,
            AmountPaid: lines.Sum(line => decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero)), PaymentMethod: "Cash");
    private static RecordInventoryTransactionDto PurchaseRequest(string key, params InventoryTransactionLineRequest[] lines) =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), lines, key);
    private static async Task<User> AddMember(IServiceProvider services, Guid business, string name, BusinessRole role)
    {
        var user = new User { UserName = name, Email = name + "@example.test", EmailConfirmed = true};
        Assert.True((await services.GetRequiredService<UserManager<User>>().CreateAsync(user, "Cedar-Moon-River-593!")).Succeeded);
        var db = services.GetRequiredService<TradeNestContext>();
        db.BusinessMemberships.Add(new() { BusinessId = business, UserId = user.Id, Role = role, JoinedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return user;
    }
    private static async Task<HttpClient> Login(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string name, string password)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/auth/login", new { username = name, password });
        response.EnsureSuccessStatusCode();
        var tokens = (await response.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        return client;
    }
}
