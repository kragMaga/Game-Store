using System.Net;
using GameStore.Api.Data;
using GameStore.Api.Entities;
using GameStore.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameStore.Api.Tests;

[Collection("API integration")]
public sealed class SecurityBatchFiveTests
{
    [Fact]
    public async Task Readiness_reports_database_failure_while_liveness_still_succeeds()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.True(ready.Headers.CacheControl!.NoStore);
        using (var scope = factory.Services.CreateScope())
        {
            // A schema failure must not leak SQL/database details to a probe or
            // falsely cause a process restart through the liveness endpoint.
            await scope.ServiceProvider.GetRequiredService<GameStoreContext>()
                .Database.ExecuteSqlRawAsync("DROP TABLE Genres");
        }
        using var failed = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("Unhealthy", await failed.Content.ReadAsStringAsync());
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Verified_backup_restores_committed_wal_data_and_search_triggers_without_touching_source()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameStoreBackupTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.db");
        var snapshot = Path.Combine(directory, "snapshot.db");
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = source, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var db = new GameStoreContext(new DbContextOptionsBuilder<GameStoreContext>().UseSqlite(connection).Options);
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
            db.Games.Add(new Game { Name = "Snapshot Adventure", GenreId = 1, Price = 20, ReleasedDate = new(2026, 1, 1) });
            db.Users.Add(new User { UserName = "snapshot-user", Role = "customer" });
            await db.SaveChangesAsync();
            var user = await db.Users.SingleAsync();
            db.AuthSessions.Add(new AuthSession { Id = "snapshot-session", UserId = user.Id,
                CreatedAtUtc = DateTime.UtcNow, LastRefreshedAtUtc = DateTime.UtcNow,
                AbsoluteExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
            db.EmailOutbox.Add(new EmailOutboxMessage { Id = "snapshot-email", ProtectedPayload = "opaque-test-payload",
                CreatedAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
            await SqliteBackup.CreateVerifiedAsync(source, snapshot);
            await SqliteBackup.VerifyAsync(snapshot);
            // Refuse both same-file and existing-file destinations; never overwrite.
            await Assert.ThrowsAsync<ArgumentException>(() => SqliteBackup.CreateVerifiedAsync(source, source));
            await Assert.ThrowsAsync<IOException>(() => SqliteBackup.CreateVerifiedAsync(source, snapshot));

            await using var restoredConnection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = snapshot, Pooling = false }.ToString());
            await restoredConnection.OpenAsync();
            await using var restored = new GameStoreContext(new DbContextOptionsBuilder<GameStoreContext>().UseSqlite(restoredConnection).Options);
            Assert.Single(await restored.Users.ToListAsync());
            Assert.Single(await restored.AuthSessions.ToListAsync());
            Assert.Equal("opaque-test-payload", (await restored.EmailOutbox.SingleAsync()).ProtectedPayload);
            Assert.Single(await CatalogSearch.Games(restored, "snapshot").ToListAsync());
            // Exercise triggers on the restored copy, not just schema presence.
            await restored.Games.ExecuteUpdateAsync(update => update.SetProperty(game => game.Name, "Restored Quest"));
            Assert.Empty(await CatalogSearch.Games(restored, "snapshot").ToListAsync());
            Assert.Single(await CatalogSearch.Games(restored, "restored").ToListAsync());
            Assert.Equal("Snapshot Adventure", (await db.Games.AsNoTracking().SingleAsync()).Name);
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
        var path = Path.Combine(Path.GetTempPath(), "GameStoreInvalidBackup-" + Guid.NewGuid().ToString("N") + ".db");
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
