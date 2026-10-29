using System.Net;
using System.Net.Http.Json;
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
public sealed class InventoryFoundationTests
{
    [Fact]
    public async Task Pack_conversion_opening_stock_and_idempotency_preserve_one_exact_balance()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var (db, service, business, owner) = await Setup(scope.ServiceProvider);
        var product = await service.CreateProductAsync(business, owner, Product("BOTTLES"));
        var crate = await service.CreateUnitAsync(business, product.Id, owner, new("Crate", 24));
        var opening = Opening(crate.Id, 2);
        var movement = await service.OpeningStockAsync(business, product.Id, owner, opening);
        Assert.Equal(48, movement.BaseQuantity);
        Assert.Equal(24, movement.ConversionFactor);
        Assert.Equal(48, movement.BalanceAfter);
        var replay = await service.OpeningStockAsync(business, product.Id, owner, opening with { Quantity = 2.00m });
        Assert.Equal(movement.Id, replay.Id);
        Assert.Equal(48, (await service.GetProductAsync(business, product.Id, owner)).QuantityOnHand);
        Assert.Equal(1, await db.StockMovements.CountAsync());
        var conflict = await Assert.ThrowsAsync<BusinessRequestException>(() => service.OpeningStockAsync(business, product.Id, owner, opening with { Quantity = 3 }));
        Assert.Equal(409, conflict.StatusCode);
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.OpeningStockAsync(business, product.Id, owner,
            opening with { IdempotencyKey = "second-opening" }));
        var history = await service.MovementsAsync(business, product.Id, owner, 1, 20);
        Assert.Equal("Existing inventory at onboarding", Assert.Single(history.Items).Reason);
        Assert.True((await service.GetProductAsync(business, product.Id, owner)).OpeningStockRecorded);
    }

    [Fact]
    public async Task Tenant_filters_write_guards_and_composite_keys_reject_cross_business_data()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var (db, service, a, owner) = await Setup(scope.ServiceProvider);
        var businesses = scope.ServiceProvider.GetRequiredService<BusinessService>();
        var b = (await businesses.CreateAsync(owner, new("Second Store", "NGN", "second@example.test", "Africa/Lagos"))).Id;
        var categoryB = await service.CreateCategoryAsync(b, owner, new("Drinks"));
        var productB = await service.CreateProductAsync(b, owner, Product("SHARED-SKU"));
        var productA = await service.CreateProductAsync(a, owner, Product("SHARED-SKU"));
        Assert.Equal(1, await db.InventoryProducts.CountAsync()); // current scope is A
        Assert.Equal(productA.Id, (await db.InventoryProducts.SingleAsync()).Id);
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.GetProductAsync(a, productB.Id, owner));
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.CreateProductAsync(a, owner, Product("BAD-CAT") with { CategoryId = categoryB.Id }));
        // Bypass the service to verify the composite FK, not just its friendly check.
        _ = await service.GetProductAsync(a, productA.Id, owner);
        db.InventoryProducts.Add(new InventoryProduct { BusinessId = a, Name = "Invalid link", Sku = "BAD", NormalizedSku = "BAD",
            StockUnit = "Each", StockUnitId = productA.StockUnitId, CategoryId = categoryB.Id, CreatedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        using var unscoped = factory.Services.CreateScope();
        var otherDb = unscoped.ServiceProvider.GetRequiredService<TradeNestContext>();
        Assert.Empty(await otherDb.InventoryProducts.ToListAsync());
        otherDb.ProductCategories.Add(new ProductCategory { BusinessId = a, Name = "Unauthorized", NormalizedName = "UNAUTHORIZED" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => otherDb.SaveChangesAsync());
    }

    [Fact]
    public async Task Opening_failure_rolls_back_balance_flag_history_and_audit_and_can_be_retried()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var (db, service, business, owner) = await Setup(scope.ServiceProvider);
        var product = await service.CreateProductAsync(business, owner, Product("ROLLBACK"));
        var unit = (await service.UnitsAsync(business, product.Id, owner, 1, 20)).Items.Single();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailOpening BEFORE INSERT ON StockMovements BEGIN SELECT RAISE(ABORT, 'Simulated movement failure'); END");
        await Assert.ThrowsAsync<DbUpdateException>(() => service.OpeningStockAsync(business, product.Id, owner, Opening(unit.Id, 5)));
        var unchanged = await service.GetProductAsync(business, product.Id, owner);
        Assert.Equal(0, unchanged.QuantityOnHand);
        Assert.False(unchanged.OpeningStockRecorded);
        Assert.Empty(await db.StockMovements.ToListAsync());
        Assert.False(await db.BusinessAuditEvents.AnyAsync(item => item.BusinessId == business && item.Action == "OpeningStockRecorded"));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailOpening");
        Assert.Equal(5, (await service.OpeningStockAsync(business, product.Id, owner, Opening(unit.Id, 5))).BalanceAfter);
        var row = await db.StockMovements.SingleAsync();
        row.Reason = "Rewrite history";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var savedUnit = await db.ProductUnits.SingleAsync();
        savedUnit.ConversionFactorMicros = 2 * StockQuantity.Scale;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(-1, 1, false)]
    [InlineData(0.0000001, 1, true)]
    [InlineData(0.5, 1, false)]
    [InlineData(0.000001, 0.000001, true)]
    public async Task Invalid_negative_indivisible_or_overprecise_stock_is_rejected(double quantity, double factor, bool fractional)
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var (db, service, business, owner) = await Setup(scope.ServiceProvider);
        var product = await service.CreateProductAsync(business, owner, Product("PRECISION") with { AllowsFractionalStock = fractional });
        var unit = factor == 1 ? (await service.UnitsAsync(business, product.Id, owner, 1, 20)).Items.Single()
            : await service.CreateUnitAsync(business, product.Id, owner, new("Tiny", (decimal)factor, true));
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.OpeningStockAsync(business, product.Id, owner, Opening(unit.Id, (decimal)quantity)));
        Assert.Equal(0, (await service.GetProductAsync(business, product.Id, owner)).QuantityOnHand);
        Assert.Empty(await db.StockMovements.ToListAsync());
    }

    [Fact]
    public async Task Fractional_quantities_zero_openings_and_low_stock_at_threshold_are_supported()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var (_, service, business, owner) = await Setup(scope.ServiceProvider);
        var product = await service.CreateProductAsync(business, owner, Product("RICE") with
            { Name = "Rice 100%", StockUnit = "Kilogram", AllowsFractionalStock = true, ReorderLevel = 12.5m });
        var bag = await service.CreateUnitAsync(business, product.Id, owner, new("Bag", 50, true));
        Assert.Equal(12.5m, (await service.OpeningStockAsync(business, product.Id, owner, Opening(bag.Id, 0.25m))).BaseQuantity);
        var low = await service.ProductsAsync(business, owner, 1, 20, "100%", true);
        Assert.Equal(product.Id, Assert.Single(low.Items).Id);
        Assert.True(low.Items.Single().IsLowStock); // equality counts, not only strictly below
        var empty = await service.CreateProductAsync(business, owner, Product("EMPTY"));
        var each = (await service.UnitsAsync(business, empty.Id, owner, 1, 20)).Items.Single();
        await service.OpeningStockAsync(business, empty.Id, owner, Opening(each.Id, 0) with { IdempotencyKey = "empty-opening" });
        Assert.True((await service.GetProductAsync(business, empty.Id, owner)).OpeningStockRecorded);
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.ProductsAsync(business, owner, 1000, 100, null));
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.ProductsAsync(business, owner, 1, 20, new string('a', 101)));
        Assert.Throws<BusinessRequestException>(() => StockQuantity.ConvertToBase(StockQuantity.MaxBalanceMicros, StockQuantity.MaxBalanceMicros));
    }

    [Fact]
    public async Task Staff_can_read_inventory_but_cannot_configure_products_or_opening_stock()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var (db, service, business, owner) = await Setup(scope.ServiceProvider);
        var product = await service.CreateProductAsync(business, owner, Product("STAFF-READ"));
        var user = new User { UserName = "inventory-staff", Email = "inventory-staff@example.test", EmailConfirmed = true};
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await manager.CreateAsync(user, "Cedar-Moon-River-593!")).Succeeded);
        db.BusinessMemberships.Add(new BusinessMembership { BusinessId = business, UserId = user.Id, Role = BusinessRole.Staff, JoinedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = user.UserName, password = "Cedar-Moon-River-593!" });
        var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        using var read = await client.GetAsync($"/businesses/{business}/products");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(read.Headers.CacheControl!.NoStore);
        using var write = await client.PostAsJsonAsync($"/businesses/{business}/products", Product("NOT-ALLOWED"));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        var baseUnit = (await service.UnitsAsync(business, product.Id, owner, 1, 20)).Items.Single();
        using var deniedOpening = await client.PostAsJsonAsync($"/businesses/{business}/products/{product.Id}/opening-stock", Opening(baseUnit.Id, 2));
        Assert.Equal(HttpStatusCode.Forbidden, deniedOpening.StatusCode);
        await db.BusinessMemberships.Where(item => item.BusinessId == business && item.UserId == user.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsActive, false));
        using var revoked = await client.GetAsync($"/businesses/{business}/products/{product.Id}");
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [Fact]
    public async Task Concurrent_identical_opening_requests_record_stock_once()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var (_, service, business, owner) = await Setup(scope.ServiceProvider);
        var product = await service.CreateProductAsync(business, owner, Product("CONCURRENT"));
        var baseUnit = (await service.UnitsAsync(business, product.Id, owner, 1, 20)).Items.Single();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = "Bootstrap-Cedar-River-593!" });
        var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        var route = $"/businesses/{business}/products/{product.Id}/opening-stock";
        var responses = await Task.WhenAll(client.PostAsJsonAsync(route, Opening(baseUnit.Id, 10)), client.PostAsJsonAsync(route, Opening(baseUnit.Id, 10)));
        foreach (var response in responses) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); response.Dispose(); }
        Assert.Equal(10, (await service.GetProductAsync(business, product.Id, owner)).QuantityOnHand);
        Assert.Single((await service.MovementsAsync(business, product.Id, owner, 1, 20)).Items);
    }

    [Fact]
    public async Task Opening_dates_use_business_timezone_and_replay_survives_timezone_change_and_archival()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var (db, _, business, owner) = await Setup(scope.ServiceProvider);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 26, 0, 30, 0, TimeSpan.Zero));
        var service = new InventoryService(db, new BusinessAccess(db), clock);
        var product = await service.CreateProductAsync(business, owner, Product("LOCAL-DATE"));
        var unit = (await service.UnitsAsync(business, product.Id, owner, 1, 20)).Items.Single();
        var request = Opening(unit.Id, 4) with { EffectiveDate = new(2026, 10, 26) };
        var movement = await service.OpeningStockAsync(business, product.Id, owner, request);
        await service.UpdateProductAsync(business, product.Id, owner, new(product.Name, 0, false));
        // Lagos is already on the 26th, but Honolulu is still on the 25th. Existing
        // receipts remain replayable while genuinely future-dated entries fail.
        await db.Businesses.Where(item => item.Id == business)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.TimeZoneId, "Pacific/Honolulu"));
        Assert.Equal(movement.Id, (await service.OpeningStockAsync(business, product.Id, owner, request)).Id);
        var other = await service.CreateProductAsync(business, owner, Product("FUTURE-DATE"));
        var otherUnit = (await service.UnitsAsync(business, other.Id, owner, 1, 20)).Items.Single();
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.OpeningStockAsync(business, other.Id, owner,
            request with { UnitId = otherUnit.Id, IdempotencyKey = "future-opening-001" }));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CreateInventoryProductDto Product(string sku) => new("Bottled Drinks", sku, "Bottle", false);
    private static OpeningStockDto Opening(Guid unit, decimal quantity) => new(unit, quantity,
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), "Existing inventory at onboarding", "opening-request-001");
    private static async Task<(TradeNestContext Db, InventoryService Service, Guid Business, int Owner)> Setup(IServiceProvider services)
    {
        var db = services.GetRequiredService<TradeNestContext>();
        var owner = (await db.Users.SingleAsync()).Id;
        var business = await services.GetRequiredService<BusinessService>().CreateAsync(owner,
            new("Inventory Store", "NGN", "stock@example.test", "Africa/Lagos"));
        return (db, services.GetRequiredService<InventoryService>(), business.Id, owner);
    }
}
