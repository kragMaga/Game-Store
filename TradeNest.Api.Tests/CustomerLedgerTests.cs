using System.Net;
using System.Net.Http.Json;
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
public sealed class CustomerLedgerTests
{
    [Fact]
    public async Task Opening_debt_partial_sales_allocated_receipts_and_reversals_produce_derived_balances()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var d = await Setup(scope.ServiceProvider);
        var opening = await d.Ledger.OpeningAsync(d.Business, d.Customer, d.Owner, Debt(1000));
        var sale = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "partial-sale-001", 5, 100, 200));
        Assert.Equal(200, sale.Transaction.AmountPaid);
        Assert.Equal(300, sale.Transaction.Outstanding);
        var statement = await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20);
        Assert.Equal(1300, statement.Balance.Outstanding);
        Assert.Equal(1000, statement.Balance.Overdue);
        var charge = statement.Charges.Items.Single(c => c.SaleId == sale.Transaction.Id);
        var request = Pay(700, new(opening.Id, 500), new(charge.Id, 200));
        var payment = await d.Ledger.PayAsync(d.Business, d.Customer, d.Owner, request);
        Assert.Equal(payment.Id, (await d.Ledger.PayAsync(d.Business, d.Customer, d.Owner, request)).Id);
        var settled = await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20);
        Assert.Equal(600, settled.Balance.Outstanding);
        Assert.Equal(900, settled.Balance.PaymentsReceived);
        Assert.Equal(500, settled.Balance.Overdue);
        Assert.Equal(600, Assert.Single((await d.Ledger.DebtorsAsync(d.Business, d.Owner, 1, 20)).Items).Outstanding);
        var reverseRequest = new ReverseCustomerPaymentDto(Day(-1), "Receipt entered against wrong payment", "reverse-late-001");
        await d.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailPaymentReversal BEFORE INSERT ON CustomerPaymentReversals BEGIN SELECT RAISE(ABORT, 'Reversal failure'); END");
        await Assert.ThrowsAsync<DbUpdateException>(() => d.Ledger.ReverseAsync(d.Business, payment.Id, d.Owner, reverseRequest));
        Assert.Equal(600, (await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Balance.Outstanding);
        Assert.Empty(await d.Db.CustomerPaymentReversals.ToListAsync());
        await d.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailPaymentReversal");
        var reversal = await d.Ledger.ReverseAsync(d.Business, payment.Id, d.Owner, reverseRequest);
        Assert.Equal(reversal.Id, (await d.Ledger.ReverseAsync(d.Business, payment.Id, d.Owner, reverseRequest)).Id);
        Assert.Equal(1300, (await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Balance.Outstanding);
        Assert.True((await d.Ledger.PaymentsAsync(d.Business, d.Customer, d.Owner, 1, 20)).Items.Single(p => p.Id == payment.Id).IsReversed);
        Assert.Equal(95, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
        Assert.Equal(1, await d.Db.CustomerPaymentReversals.CountAsync());
        var receipt = await d.Ledger.PaymentDetailAsync(d.Business, payment.Id, d.Owner);
        Assert.Equal(2, receipt.Allocations.Count);
        Assert.Equal(700, receipt.Allocations.Sum(a => a.Amount));
        Assert.Equal(reversal.Id, receipt.Reversal!.Id);
        var saved = await d.Db.CustomerPayments.SingleAsync(p => p.Id == payment.Id);
        saved.AmountMinor = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => d.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Staff_can_take_sale_payment_but_later_receipts_and_statements_require_live_granted_permission()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var d = await Setup(scope.ServiceProvider);
        var staff = new User { UserName = "debt-staff", Email = "debt-staff@example.test", EmailConfirmed = true};
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(staff, "Cedar-Moon-River-593!")).Succeeded);
        d.Db.BusinessMemberships.Add(new() { BusinessId = d.Business, UserId = staff.Id, Role = BusinessRole.Staff, JoinedAtUtc = DateTime.UtcNow });
        await d.Db.SaveChangesAsync();
        using var client = await Login(factory, staff.UserName!, "Cedar-Moon-River-593!");
        var path = $"/businesses/{d.Business}/customers/{d.Customer}";
        using var saleResponse = await client.PostAsJsonAsync($"/businesses/{d.Business}/sales", Sale(d, "staff-credit-sale", 2, 100, 100));
        Assert.Equal(HttpStatusCode.OK, saleResponse.StatusCode);
        using var noStatement = await client.GetAsync(path + "/statement");
        Assert.Equal(HttpStatusCode.Forbidden, noStatement.StatusCode);
        var charge = Assert.Single((await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Charges.Items);
        var payment = Pay(30, new PaymentAllocationRequest(charge.Id, 30));
        using var denied = await client.PostAsJsonAsync(path + "/payments", payment);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var members = scope.ServiceProvider.GetRequiredService<BusinessService>();
        await members.UpdateMemberAsync(d.Business, d.Owner, staff.Id, new("Staff", true, true));
        using var allowed = await client.PostAsJsonAsync(path + "/payments", payment);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var recorded = (await allowed.Content.ReadFromJsonAsync<CustomerPaymentDto>())!;
        using var statementResponse = await client.GetAsync(path + "/statement");
        Assert.Equal(70, (await statementResponse.Content.ReadFromJsonAsync<CustomerStatementDto>())!.Balance.Outstanding);
        using var reversalDenied = await client.PostAsJsonAsync($"/businesses/{d.Business}/customer-payments/{recorded.Id}/reverse",
            new ReverseCustomerPaymentDto(Day(-1), "Staff must not reverse receipts", "staff-reverse-001"));
        Assert.Equal(HttpStatusCode.Forbidden, reversalDenied.StatusCode);
        await members.UpdateMemberAsync(d.Business, d.Owner, staff.Id, new("Staff", false, true));
        using var revoked = await client.PostAsJsonAsync(path + "/payments", payment with { IdempotencyKey = "revoked-late-001" });
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    [Fact]
    public async Task Walk_in_credit_and_overpayment_are_rejected_but_explicit_full_payment_is_recorded()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var d = await Setup(scope.ServiceProvider);
        foreach (var paid in new decimal?[] { null, 50, 101 })
            await Assert.ThrowsAsync<BusinessRequestException>(() => d.Sales.RecordAsync(d.Business, d.Owner, "Sale",
                Sale(d, "invalid-walk-in", 1, 100, paid) with { CustomerId = null }));
        Assert.Equal(100, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
        Assert.Empty(await d.Db.CustomerCharges.ToListAsync());
        var cash = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "valid-walk-in-001", 1, 100, 100) with { CustomerId = null });
        Assert.True(cash.Transaction.PaymentsTracked);
        Assert.Equal(0, cash.Transaction.Outstanding);
        var free = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "free-walk-in-001", 1, 0, null) with { CustomerId = null });
        Assert.Equal(0, free.Transaction.Outstanding);
        Assert.Equal(0, free.Transaction.AmountPaid);
        var payment = await d.Db.CustomerPayments.SingleAsync();
        await Assert.ThrowsAsync<BusinessRequestException>(() => d.Ledger.ReverseAsync(d.Business, payment.Id, d.Owner,
            new(Day(-1), "Walk-in correction needs sale reversal", "walk-in-reverse")));
    }

    [Fact]
    public async Task Invalid_allocations_cross_customer_overpayment_and_precision_never_change_debt()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var d = await Setup(scope.ServiceProvider);
        var charge = await d.Ledger.OpeningAsync(d.Business, d.Customer, d.Owner, Debt(100));
        var other = await d.Ledger.CreateAsync(d.Business, d.Owner, new("OTHER", "Other Customer"));
        var otherCharge = await d.Ledger.OpeningAsync(d.Business, other.Id, d.Owner, Debt(100) with { IdempotencyKey = "other-debt-001" });
        var bad = new[] { Pay(100, new PaymentAllocationRequest(otherCharge.Id, 100)),
            Pay(101, new PaymentAllocationRequest(charge.Id, 101)), Pay(100, new PaymentAllocationRequest(charge.Id, 99)),
            Pay(0.001m, new PaymentAllocationRequest(charge.Id, 0.001m)), Pay(100, new(charge.Id, 50), new(charge.Id, 50)),
            Pay(10, new PaymentAllocationRequest(charge.Id, 10)) with { EffectiveDate = Day(-9) } };
        foreach (var request in bad) await Assert.ThrowsAsync<BusinessRequestException>(() => d.Ledger.PayAsync(d.Business, d.Customer, d.Owner, request));
        Assert.Empty(await d.Db.CustomerPayments.ToListAsync());
        Assert.Equal(100, (await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Balance.Outstanding);
        await Assert.ThrowsAsync<BusinessRequestException>(() => d.Ledger.OpeningAsync(d.Business, d.Customer, d.Owner,
            Debt(200) with { IdempotencyKey = "second-opening-debt" }));
        var saved = await d.Ledger.OpeningAsync(d.Business, d.Customer, d.Owner, Debt(100));
        Assert.Equal(charge.Id, saved.Id);
    }

    [Fact]
    public async Task Customers_and_financial_records_are_tenant_scoped_and_archived_debt_stays_payable()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var d = await Setup(scope.ServiceProvider);
        var businessB = await scope.ServiceProvider.GetRequiredService<BusinessService>().CreateAsync(d.Owner,
            new("Second Tenant", "NGN", "second@example.test", "Africa/Lagos"));
        var foreign = await d.Ledger.CreateAsync(businessB.Id, d.Owner, new("MAIN", "Foreign Customer"));
        await Assert.ThrowsAsync<BusinessRequestException>(() => d.Sales.RecordAsync(d.Business, d.Owner, "Sale",
            Sale(d, "foreign-customer", 1, 100, 0) with { CustomerId = foreign.Id }));
        await Assert.ThrowsAsync<BusinessRequestException>(() => d.Ledger.StatementAsync(d.Business, foreign.Id, d.Owner, 1, 20));
        var charge = await d.Ledger.OpeningAsync(d.Business, d.Customer, d.Owner, Debt(100));
        await d.Ledger.UpdateAsync(d.Business, d.Customer, d.Owner, new("Archived Customer", false));
        Assert.Single((await d.Ledger.DebtorsAsync(d.Business, d.Owner, 1, 20)).Items);
        await Assert.ThrowsAsync<BusinessRequestException>(() => d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "archived-sale", 1, 100, 0)));
        await d.Ledger.PayAsync(d.Business, d.Customer, d.Owner, Pay(100, new PaymentAllocationRequest(charge.Id, 100)));
        Assert.Empty((await d.Ledger.DebtorsAsync(d.Business, d.Owner, 1, 20)).Items);
        using var unscoped = factory.Services.CreateScope();
        Assert.Empty(await unscoped.ServiceProvider.GetRequiredService<TradeNestContext>().CustomerPayments.ToListAsync());
    }

    [Fact]
    public async Task Failed_sale_payment_and_later_allocation_roll_back_stock_and_financial_records()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var d = await Setup(scope.ServiceProvider);
        await d.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailCustomerPayment BEFORE INSERT ON CustomerPayments BEGIN SELECT RAISE(ABORT, 'Payment failure'); END");
        var request = Sale(d, "atomic-sale-001", 1, 100, 100);
        await Assert.ThrowsAsync<DbUpdateException>(() => d.Sales.RecordAsync(d.Business, d.Owner, "Sale", request));
        Assert.Equal(100, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
        Assert.Empty(await d.Db.InventoryTransactions.ToListAsync());
        Assert.Empty(await d.Db.CustomerCharges.ToListAsync());
        await d.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailCustomerPayment");
        await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", request with { AmountPaid = 0, PaymentMethod = null });
        var charge = Assert.Single((await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Charges.Items);
        await d.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailCustomerAllocation BEFORE INSERT ON CustomerPaymentAllocations BEGIN SELECT RAISE(ABORT, 'Allocation failure'); END");
        await Assert.ThrowsAsync<DbUpdateException>(() => d.Ledger.PayAsync(d.Business, d.Customer, d.Owner, Pay(100, new PaymentAllocationRequest(charge.Id, 100))));
        Assert.Empty(await d.Db.CustomerPayments.ToListAsync());
        Assert.Equal(100, (await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Balance.Outstanding);
        await d.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailCustomerAllocation");
        await d.Ledger.PayAsync(d.Business, d.Customer, d.Owner, Pay(100, new PaymentAllocationRequest(charge.Id, 100)));
        Assert.Equal(0, (await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Balance.Outstanding);
    }

    [Fact]
    public async Task Concurrent_receipts_and_retries_cannot_allocate_the_same_debt_twice()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var d = await Setup(scope.ServiceProvider);
        var charge = await d.Ledger.OpeningAsync(d.Business, d.Customer, d.Owner, Debt(100));
        using var client = await Login(factory, "Admin", "Bootstrap-Cedar-River-593!");
        var path = $"/businesses/{d.Business}/customers/{d.Customer}/payments";
        var request = Pay(40, new PaymentAllocationRequest(charge.Id, 40));
        var duplicates = await Task.WhenAll(client.PostAsJsonAsync(path, request), client.PostAsJsonAsync(path, request));
        foreach (var response in duplicates) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); response.Dispose(); }
        var remaining = Pay(60, new PaymentAllocationRequest(charge.Id, 60));
        var competing = await Task.WhenAll(client.PostAsJsonAsync(path, remaining with { IdempotencyKey = "competing-payment-a" }),
            client.PostAsJsonAsync(path, remaining with { IdempotencyKey = "competing-payment-b" }));
        Assert.Single(competing, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(competing, r => r.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in competing) response.Dispose();
        Assert.Equal(2, await d.Db.CustomerPayments.CountAsync());
        Assert.Equal(0, (await d.Ledger.StatementAsync(d.Business, d.Customer, d.Owner, 1, 20)).Balance.Outstanding);
        Assert.Equal(100, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
    }

    [Fact]
    public async Task Legacy_sales_remain_financially_untracked_instead_of_being_assumed_paid_or_owed()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var d = await Setup(scope.ServiceProvider);
        var sale = await d.Sales.RecordAsync(d.Business, d.Owner, "Sale", Sale(d, "legacy-sale-001", 1, 100, 100));
        d.Db.ChangeTracker.Clear();
        var migrations = d.Db.GetService<IMigrator>();
        await migrations.MigrateAsync("20261026002536_AddPurchasesAndSales");
        await migrations.MigrateAsync();
        var legacy = await d.Sales.GetAsync(d.Business, sale.Transaction.Id, d.Owner, "Sale");
        Assert.False(legacy.Transaction.PaymentsTracked);
        Assert.Null(legacy.Transaction.AmountPaid);
        Assert.Null(legacy.Transaction.Outstanding);
        Assert.Equal(99, (await d.Inventory.GetProductAsync(d.Business, d.Product, d.Owner)).QuantityOnHand);
    }

    private sealed record Data(TradeNestContext Db, CustomerLedgerService Ledger, InventoryTransactionService Sales,
        InventoryService Inventory, Guid Business, int Owner, Guid Product, Guid Unit, Guid Customer);
    private static async Task<Data> Setup(IServiceProvider services)
    {
        var db = services.GetRequiredService<TradeNestContext>();
        var owner = (await db.Users.SingleAsync()).Id;
        var business = await services.GetRequiredService<BusinessService>().CreateAsync(owner, new("Ledger Store", "NGN", "ledger@example.test", "Africa/Lagos"));
        var inventory = services.GetRequiredService<InventoryService>();
        var product = await inventory.CreateProductAsync(business.Id, owner, new("Ledger Product", "LEDGER", "Each", false));
        var unit = (await inventory.UnitsAsync(business.Id, product.Id, owner, 1, 20)).Items.Single();
        await inventory.OpeningStockAsync(business.Id, product.Id, owner, new(unit.Id, 100, Day(-10), "Opening goods before sales", "ledger-stock-opening"));
        var ledger = services.GetRequiredService<CustomerLedgerService>();
        var customer = await ledger.CreateAsync(business.Id, owner, new("MAIN", "Main Customer"));
        return new(db, ledger, services.GetRequiredService<InventoryTransactionService>(), inventory, business.Id, owner, product.Id, unit.Id, customer.Id);
    }
    private static DateOnly Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(offset));
    private static OpeningCustomerDebtDto Debt(decimal amount) => new(amount, Day(-8), "Existing customer debt at onboarding", "opening-debt-001", Day(-1));
    private static RecordCustomerPaymentDto Pay(decimal amount, params PaymentAllocationRequest[] allocations) => new(amount, Day(-1), "BankTransfer", allocations, "later-payment-001");
    private static RecordInventoryTransactionDto Sale(Data d, string key, decimal quantity, decimal price, decimal? paid) =>
        new(Day(-2), new[] { new InventoryTransactionLineRequest(d.Product, d.Unit, quantity, price) }, key,
            CustomerId: d.Customer, AmountPaid: paid, PaymentMethod: paid > 0 ? "Cash" : null);
    private static async Task<HttpClient> Login(TradeNestWebApplicationFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        var tokens = (await response.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        return client;
    }
}
