using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class BusinessMasterDataTests
{
    private static readonly DateOnly Today = new(2026, 1, 1);

    [Fact]
    public async Task Master_routes_accept_selected_ids_and_staff_can_read_but_cannot_change_lists_or_read_financial_reports()
    {
        using var factory = new TradeNestWebApplicationFactory(); using var scope = factory.Services.CreateScope();
        var p = scope.ServiceProvider; var db = p.GetRequiredService<TradeNestContext>(); var owner = (await db.Users.SingleAsync()).Id;
        var business = (await p.GetRequiredService<BusinessService>().CreateAsync(owner, new("API store", "NGN", "api@example.test", "Africa/Lagos"))).Id;
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/businesses/{business}/units-of-measure")).StatusCode);
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = "Bootstrap-Cedar-River-593!" });
        login.EnsureSuccessStatusCode(); var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        using var created = await client.PostAsJsonAsync($"/businesses/{business}/units-of-measure", new { name = "Piece" });
        created.EnsureSuccessStatusCode(); var measure = (await created.Content.ReadFromJsonAsync<UnitOfMeasureDto>())!;
        using var product = await client.PostAsJsonAsync($"/businesses/{business}/products", new { name = "Selected master", sku = "ID-ONLY", stockUnitId = measure.Id, allowsFractionalStock = false });
        product.EnsureSuccessStatusCode();
        using var report = await client.GetAsync($"/businesses/{business}/reports/categories/inventory"); report.EnsureSuccessStatusCode();
        using var export = await client.GetAsync($"/businesses/{business}/reports/categories/inventory/export.xlsx"); export.EnsureSuccessStatusCode();
        var staff = new User { UserName = "master.staff", Email = "staff@example.test", EmailConfirmed = true };
        Assert.True((await p.GetRequiredService<UserManager<User>>().CreateAsync(staff, "Cedar-Moon-River-593!")).Succeeded);
        db.BusinessMemberships.Add(new() { BusinessId = business, UserId = staff.Id, Role = BusinessRole.Staff, JoinedAtUtc = DateTime.UtcNow }); await db.SaveChangesAsync();
        var inventory = p.GetRequiredService<InventoryService>();
        Assert.Single((await inventory.MeasuresAsync(business, staff.Id, 1, 10)).Items);
        Assert.Equal(403, (await Assert.ThrowsAsync<BusinessRequestException>(() => inventory.CreateMeasureAsync(business, staff.Id, new("Carton")))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<BusinessRequestException>(() => inventory.UpdateMasterAsync(business, measure.Id, staff.Id, new("Piece", false), false))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<BusinessRequestException>(() => p.GetRequiredService<InventoryReportService>().CategoriesAsync(business, staff.Id, "sales", null, null))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessRequestException>(() => inventory.MeasuresAsync(Guid.NewGuid(), owner, 1, 10))).StatusCode);
    }

    [Fact]
    public async Task Master_lists_are_tenant_owned_reusable_and_deactivated_without_rewriting_conversions()
    {
        using var factory = new TradeNestWebApplicationFactory(); using var scope = factory.Services.CreateScope();
        var p = scope.ServiceProvider; var db = p.GetRequiredService<TradeNestContext>();
        var owner = (await db.Users.SingleAsync()).Id; var inventory = p.GetRequiredService<InventoryService>();
        var businesses = p.GetRequiredService<BusinessService>();
        var a = (await businesses.CreateAsync(owner, new("Store one", "NGN", "one@example.test", "Africa/Lagos"))).Id;
        var b = (await businesses.CreateAsync(owner, new("Store two", "NGN", "two@example.test", "Africa/Lagos"))).Id;
        var each = await inventory.CreateMeasureAsync(a, owner, new("Piece"));
        var carton = await inventory.CreateMeasureAsync(a, owner, new("Carton"));
        var foreign = await inventory.CreateMeasureAsync(b, owner, new("Piece"));
        var first = await inventory.CreateProductAsync(a, owner, new("First product", "ONE", null, false, StockUnitId: each.Id));
        var second = await inventory.CreateProductAsync(a, owner, new("Second product", "TWO", "Piece", false));
        Assert.Equal(each.Id, second.StockUnitId);
        var pack1 = await inventory.CreateUnitAsync(a, first.Id, owner, new(null, 12, UnitOfMeasureId: carton.Id));
        var pack2 = await inventory.CreateUnitAsync(a, second.Id, owner, new(null, 24, UnitOfMeasureId: carton.Id));
        Assert.Equal(pack1.UnitOfMeasureId, pack2.UnitOfMeasureId);
        Assert.Equal(12m, pack1.ConversionFactor); Assert.Equal(24m, pack2.ConversionFactor);
        await Assert.ThrowsAsync<BusinessRequestException>(() => inventory.CreateProductAsync(a, owner, new("Bad product", "BAD", null, false, StockUnitId: foreign.Id)));
        await Assert.ThrowsAsync<BusinessRequestException>(() => inventory.CreateMeasureAsync(a, owner, new(" piece ")));
        await inventory.UpdateMasterAsync(a, each.Id, owner, new("Piece", false), false);
        await Assert.ThrowsAsync<BusinessRequestException>(() => inventory.CreateProductAsync(a, owner, new("Inactive unit", "OFF", "Piece", false)));
        Assert.Equal(each.Id, (await inventory.GetProductAsync(a, first.Id, owner)).StockUnitId);
        Assert.Equal(2, (await inventory.MeasuresAsync(a, owner, 1, 100)).TotalItems);
        var existing = await db.InventoryProducts.SingleAsync(x => x.Id == first.Id);
        existing.StockUnitId = carton.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        // A direct EF write must fail the composite FK, independently of service validation.
        db.InventoryProducts.Add(new InventoryProduct { BusinessId = a, Name = "Foreign unit", Sku = "FK", NormalizedSku = "FK", StockUnit = "Piece", StockUnitId = foreign.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Category_reports_preserve_posting_category_and_original_return_category_and_separate_units()
    {
        using var factory = new TradeNestWebApplicationFactory(); using var scope = factory.Services.CreateScope();
        var p = scope.ServiceProvider; var db = p.GetRequiredService<TradeNestContext>(); var owner = (await db.Users.SingleAsync()).Id;
        var inventory = p.GetRequiredService<InventoryService>(); var reports = p.GetRequiredService<InventoryReportService>();
        var transactions = p.GetRequiredService<InventoryTransactionService>();
        var business = (await p.GetRequiredService<BusinessService>().CreateAsync(owner, new("Reporting store", "NGN", "reports@example.test", "Africa/Lagos"))).Id;
        var drinks = await inventory.CreateCategoryAsync(business, owner, new("Drinks"));
        var food = await inventory.CreateCategoryAsync(business, owner, new("Food"));
        var bottle = await inventory.CreateProductAsync(business, owner, new("Water bottles", "WATER", "Piece", false, CategoryId: drinks.Id));
        var rice = await inventory.CreateProductAsync(business, owner, new("Rice bags", "RICE", "Kilogram", true, CategoryId: drinks.Id));
        var unit = (await inventory.UnitsAsync(business, bottle.Id, owner, 1, 10)).Items.Single();
        var kg = (await inventory.UnitsAsync(business, rice.Id, owner, 1, 10)).Items.Single();
        await inventory.OpeningStockAsync(business, bottle.Id, owner, new(unit.Id, 20, Today, "Initial stock", "open-bottles"));
        await inventory.OpeningStockAsync(business, rice.Id, owner, new(kg.Id, 5, Today, "Initial stock", "open-ricebags"));
        var sale = await transactions.RecordAsync(business, owner, "Sale", new(Today,
            [new(bottle.Id, unit.Id, 3, 50), new(rice.Id, kg.Id, 1.5m, 100)], "sale-categories", AmountPaid: 300, PaymentMethod: "Cash"));
        await inventory.UpdateProductAsync(business, bottle.Id, owner, new("Water bottles", 0, true, food.Id));
        await inventory.UpdateMasterAsync(business, drinks.Id, owner, new("Beverages", false), true);
        await p.GetRequiredService<InventoryCorrectionService>().ReturnAsync(business, sale.Transaction.Id, owner,
            new(Today, "Returned bottle", "return-category", [new(sale.Lines.First().Id, 1)], RefundAmount: 50, RefundMethod: "Cash"));
        var sales = await reports.CategoriesAsync(business, owner, "sales", Today, Today, drinks.Id);
        Assert.Equal(2, sales.Rows.Count); // Pieces and kilograms must never be combined.
        Assert.All(sales.Rows, row => Assert.Equal("Drinks", row[1]));
        Assert.All(sales.Rows, row => Assert.Equal(150m, row[11]));
        Assert.Empty((await reports.CategoriesAsync(business, owner, "sales", Today, Today, food.Id)).Rows);
        var returns = await reports.CategoriesAsync(business, owner, "corrections", Today, Today, drinks.Id);
        var returned = Assert.Single(returns.Rows); Assert.Equal("Drinks", returned[1]); Assert.Equal(50m, returned[11]);
        Assert.Equal(1m, returned[7]);
        var current = await reports.CategoriesAsync(business, owner, "inventory", null, null, food.Id);
        Assert.Equal(18m, Assert.Single(current.Rows)[7]);
        Assert.NotEmpty((await reports.CategoriesAsync(business, owner, "movements", Today, Today)).Rows);
        Assert.Empty((await reports.CategoriesAsync(business, owner, "purchases", Today, Today)).Rows);
        Assert.Empty((await reports.CategoriesAsync(business, owner, "sales", Today.AddDays(1), Today.AddDays(1))).Rows);
        Assert.Single((await reports.CategoriesAsync(business, owner, "sales", Today, Today, size: 1)).Rows);
        Assert.NotEmpty(InventoryWorkbook.Create(sales));
        await Assert.ThrowsAsync<BusinessRequestException>(() => inventory.CreateProductAsync(business, owner, new("Inactive category", "INACTIVE", "Piece", false, CategoryId: drinks.Id)));
    }

    [Fact]
    public async Task Upgrade_backfills_unit_relationships_and_keeps_unknown_historical_categories_explicit()
    {
        using var factory = new TradeNestWebApplicationFactory(); using var scope = factory.Services.CreateScope();
        var p = scope.ServiceProvider; var db = p.GetRequiredService<TradeNestContext>(); var owner = (await db.Users.SingleAsync()).Id;
        var inventory = p.GetRequiredService<InventoryService>();
        var business = (await p.GetRequiredService<BusinessService>().CreateAsync(owner, new("Upgrade store", "NGN", "upgrade@example.test", "Africa/Lagos"))).Id;
        var category = await inventory.CreateCategoryAsync(business, owner, new("General"));
        var product = await inventory.CreateProductAsync(business, owner, new("Existing product", "OLD", "Piece", false, CategoryId: category.Id));
        var unit = (await inventory.UnitsAsync(business, product.Id, owner, 1, 10)).Items.Single();
        await inventory.OpeningStockAsync(business, product.Id, owner, new(unit.Id, 10, Today, "Opening stock", "upgrade-opening"));
        db.ChangeTracker.Clear(); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(db.Database.GetMigrations().Single(x => x.EndsWith("_RemoveLegacyArchives")));
        await migrator.MigrateAsync(); db.ChangeTracker.Clear();
        var upgraded = await inventory.GetProductAsync(business, product.Id, owner);
        Assert.NotEqual(Guid.Empty, upgraded.StockUnitId); Assert.Equal(10m, upgraded.QuantityOnHand);
        Assert.True((await inventory.CategoriesAsync(business, owner, 1, 10)).Items.Single().IsActive);
        Assert.Equal(upgraded.StockUnitId, (await inventory.UnitsAsync(business, product.Id, owner, 1, 10)).Items.Single().UnitOfMeasureId);
        var movement = await db.StockMovements.SingleAsync(); Assert.False(movement.CategoryRecorded); Assert.Null(movement.CategoryId);
        var history = await p.GetRequiredService<InventoryReportService>().CategoriesAsync(business, owner, "movements", Today, Today);
        Assert.Equal("Historical category not recorded", Assert.Single(history.Rows)[1]);
        Assert.Equal(1, Assert.Single(await db.Database.SqlQueryRaw<int>("PRAGMA foreign_keys").ToListAsync()));
        Assert.Empty(await db.Database.SqlQueryRaw<string>("""SELECT "table" AS Value FROM pragma_foreign_key_check""").ToListAsync());
    }
}
