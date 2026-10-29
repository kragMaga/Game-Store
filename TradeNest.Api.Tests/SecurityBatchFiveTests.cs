using System.Net;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class SecurityBatchFiveTests
{
    [Fact]
    public async Task Readiness_reports_database_failure_while_liveness_still_succeeds()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var client = factory.CreateClient();
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.True(ready.Headers.CacheControl!.NoStore);
        using (var scope = factory.Services.CreateScope())
        {
            // A schema failure must not leak SQL/database details to a probe or
            // falsely cause a process restart through the liveness endpoint.
            await scope.ServiceProvider.GetRequiredService<TradeNestContext>()
                .Database.ExecuteSqlRawAsync("DROP TABLE Businesses");
        }
        using var failed = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("Unhealthy", await failed.Content.ReadAsStringAsync());
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Verified_backup_restores_committed_inventory_and_account_data_without_touching_source()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TradeNestBackupTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.db");
        var snapshot = Path.Combine(directory, "snapshot.db");
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = source, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var db = new TradeNestContext(new DbContextOptionsBuilder<TradeNestContext>().UseSqlite(connection).Options);
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
            db.Users.Add(new User { UserName = "snapshot-user"});
            await db.SaveChangesAsync();
            var user = await db.Users.SingleAsync();
            // Tenant ownership and membership must survive snapshots together
            // with the pre-existing catalog/account/session data.
            var business = new Business { Name = "Snapshot Business", Currency = "NGN",
                NotificationEmail = "snapshot@example.test", TimeZoneId = "Africa/Lagos", OwnerUserId = user.Id,
                CreatedAtUtc = DateTime.UtcNow };
            db.Businesses.Add(business);
            db.BusinessMemberships.Add(new BusinessMembership { BusinessId = business.Id, UserId = user.Id,
                Role = BusinessRole.Owner, JoinedAtUtc = DateTime.UtcNow });
            db.AuthSessions.Add(new AuthSession { Id = "snapshot-session", UserId = user.Id,
                CreatedAtUtc = DateTime.UtcNow, LastRefreshedAtUtc = DateTime.UtcNow,
                AbsoluteExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
            db.EmailOutbox.Add(new EmailOutboxMessage { Id = "snapshot-email", ProtectedPayload = "opaque-test-payload",
                CreatedAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
            var inventory = new InventoryService(db, new BusinessAccess(db), TimeProvider.System);
            var stockProduct = await inventory.CreateProductAsync(business.Id, user.Id,
                new("Snapshot Stock", "SNAP-STOCK", "Each", false));
            var baseUnit = (await inventory.UnitsAsync(business.Id, stockProduct.Id, user.Id, 1, 20)).Items.Single();
            await inventory.OpeningStockAsync(business.Id, stockProduct.Id, user.Id,
                new(baseUnit.Id, 42, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), "Snapshot opening balance", "backup-opening-001"));
            // A restored sale must retain its immutable price/unit snapshots and
            // the matching negative stock movement, not just the current balance.
            var ledger = new CustomerLedgerService(db, new BusinessAccess(db), TimeProvider.System);
            var snapshotCustomer = await ledger.CreateAsync(business.Id, user.Id, new("SNAP-CUSTOMER", "Snapshot Customer"));
            var openingDebt = await ledger.OpeningAsync(business.Id, snapshotCustomer.Id, user.Id,
                new(100, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), "Snapshot customer opening debt", "backup-opening-debt"));
            var transactions = new InventoryTransactionService(db, new BusinessAccess(db), TimeProvider.System, ledger);
            var sale = await transactions.RecordAsync(business.Id, user.Id, "Sale",
                new(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
                    new[] { new InventoryTransactionLineRequest(stockProduct.Id, baseUnit.Id, 2, 125.5m) }, "backup-sale-001",
                    CustomerId: snapshotCustomer.Id, AmountPaid: 51, PaymentMethod: "Cash"));
            var receipt = await ledger.PayAsync(business.Id, snapshotCustomer.Id, user.Id,
                new(100, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), "BankTransfer",
                    new[] { new PaymentAllocationRequest(openingDebt.Id, 100) }, "backup-later-receipt"));
            await ledger.ReverseAsync(business.Id, receipt.Id, user.Id,
                new(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), "Snapshot receipt correction", "backup-reversal"));
            // Corrections and daily deduplication markers must survive restore,
            // alongside the original sale and receipt evidence they refer to.
            var corrections = new InventoryCorrectionService(db, new BusinessAccess(db), TimeProvider.System);
            await corrections.ReturnAsync(business.Id, sale.Transaction.Id, user.Id,
                new(DateOnly.FromDateTime(DateTime.UtcNow), "Snapshot damaged return", "backup-return-001",
                    new[] { new ReturnLineDto(sale.Lines.Single().Id, 1, false) }));
            db.BusinessNotificationDispatches.Add(new() { BusinessId = business.Id, LocalDate = DateOnly.FromDateTime(DateTime.UtcNow), CreatedAtUtc = DateTime.UtcNow });
            // Operational metadata and price defaults are part of the same snapshot.
            var savedProduct = await db.InventoryProducts.SingleAsync(p => p.Id == stockProduct.Id);
            savedProduct.DefaultSellingPriceMicros = 150_000_000;
            db.BusinessNotifications.Add(new() { BusinessId = business.Id, Kind = "LowStock", LocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Recipient = "snapshot@example.test", OutboxMessageId = "snapshot-email", ProtectedPayload = "protected-business-retry",
                CreatedAtUtc = DateTime.UtcNow, RetryUntilUtc = DateTime.UtcNow.AddDays(7) });
            db.DebtReminderDispatches.Add(new() { BusinessId = business.Id, LocalDate = DateOnly.FromDateTime(DateTime.UtcNow), CreatedAtUtc = DateTime.UtcNow });
            db.InventoryImportBatches.Add(new() { BusinessId = business.Id, Id = Guid.NewGuid(), Kind = "products", ActorUserId = user.Id, RowCount = 1, CommittedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await SqliteBackup.CreateVerifiedAsync(source, snapshot);
            await SqliteBackup.VerifyAsync(snapshot);
            // Refuse both same-file and existing-file destinations; never overwrite.
            await Assert.ThrowsAsync<ArgumentException>(() => SqliteBackup.CreateVerifiedAsync(source, source));
            await Assert.ThrowsAsync<IOException>(() => SqliteBackup.CreateVerifiedAsync(source, snapshot));

            await using var restoredConnection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = snapshot, Pooling = false }.ToString());
            await restoredConnection.OpenAsync();
            await using var restored = new TradeNestContext(new DbContextOptionsBuilder<TradeNestContext>().UseSqlite(restoredConnection).Options);
            Assert.Single(await restored.Users.ToListAsync());
            // New backups must not bring back the deleted legacy tables.
            Assert.Equal(0, await restored.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE name IN ('ArchivedGames','ArchivedGenres','ArchivedGlobalRoles')").SingleAsync());
            Assert.Single(await restored.AuthSessions.ToListAsync());
            Assert.Equal("Snapshot Business", (await restored.Businesses.SingleAsync()).Name);
            Assert.Equal(BusinessRole.Owner, (await restored.BusinessMemberships.SingleAsync()).Role);
            var restoredInventory = new InventoryService(restored, new BusinessAccess(restored), TimeProvider.System);
            Assert.Equal(40, (await restoredInventory.GetProductAsync(business.Id, stockProduct.Id, user.Id)).QuantityOnHand);
            Assert.Equal(3, (await restoredInventory.MovementsAsync(business.Id, stockProduct.Id, user.Id, 1, 20)).Items.Count);
            var restoredTransactions = new InventoryTransactionService(restored, new BusinessAccess(restored), TimeProvider.System,
                new CustomerLedgerService(restored, new BusinessAccess(restored), TimeProvider.System));
            var restoredSale = await restoredTransactions.GetAsync(business.Id, sale.Transaction.Id, user.Id, "Sale");
            Assert.Equal(251, restoredSale.Transaction.Total);
            Assert.Equal(125.5m, Assert.Single(restoredSale.Lines).UnitPrice);
            Assert.Equal(74.5m, restoredSale.Transaction.Outstanding);
            var restoredLedger = new CustomerLedgerService(restored, new BusinessAccess(restored), TimeProvider.System);
            Assert.Equal(174.5m, (await restoredLedger.StatementAsync(business.Id, snapshotCustomer.Id, user.Id, 1, 20)).Balance.Outstanding);
            Assert.Single(await restored.InventoryCorrections.ToListAsync());
            Assert.Single(await restored.CustomerChargeAdjustments.ToListAsync());
            Assert.Single(await restored.BusinessNotificationDispatches.ToListAsync());
            Assert.Equal("protected-business-retry", (await restored.BusinessNotifications.SingleAsync()).ProtectedPayload);
            Assert.Single(await restored.DebtReminderDispatches.ToListAsync());
            Assert.Single(await restored.InventoryImportBatches.ToListAsync());
            var restoredNumbers = await restored.DocumentSequences.ToDictionaryAsync(n => n.Prefix, n => n.LastNumber);
            Assert.Equal(1, restoredNumbers["SALE"]); Assert.Equal(2, restoredNumbers["PAY"]);
            Assert.Equal(1, restoredNumbers["COR"]); Assert.Equal(1, restoredNumbers["REV"]);
            Assert.Equal(150, (await restoredInventory.GetProductAsync(business.Id, stockProduct.Id, user.Id)).DefaultSellingPrice);
            Assert.True((await restoredLedger.PaymentDetailAsync(business.Id, receipt.Id, user.Id)).Payment.IsReversed);
            Assert.Equal("opaque-test-payload", (await restored.EmailOutbox.SingleAsync()).ProtectedPayload);

        }
        finally
        {
            // Delete only the explicitly created test files; no recursive cleanup.
            foreach (var path in new[] { source, snapshot })
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task Backup_verification_rejects_an_unrelated_database_and_missing_source()
    {
        var path = Path.Combine(Path.GetTempPath(), "TradeNestInvalidBackup-" + Guid.NewGuid().ToString("N") + ".db");
        var missing = path + ".missing";
        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Unrelated (Id INTEGER PRIMARY KEY)";
                await command.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteBackup.VerifyAsync(path));
            await Assert.ThrowsAsync<SqliteException>(() => SqliteBackup.CreateVerifiedAsync(missing, path));
            Assert.False(File.Exists(missing));
        }
        finally { File.Delete(path); }
    }
}
