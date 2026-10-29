using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace TradeNest.Api.Data;

// Microsoft.Data.Sqlite starts non-deferred serializable transactions by default.
// Take the writer lock before reading security state, including the admin count.
// Shared by multi-step security mutations. Use "await using" so every early return
// or exception disposes the transaction and rolls back unless CommitAsync was called.
internal sealed class SecurityTransaction(TradeNestContext db, IDbContextTransaction transaction) : IAsyncDisposable
{
    public static async Task<SecurityTransaction> BeginAsync(TradeNestContext db)
    {
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        // Reload account state inside the transaction instead of reusing entities
        // that may have been loaded before another request changed them.
        db.ChangeTracker.Clear();
        return new SecurityTransaction(db, transaction);
    }

    // Call only after every security update succeeds. An early return or exception
    // leaves the transaction uncommitted, so disposal rolls its changes back.
    public Task CommitAsync() => transaction.CommitAsync();

    public async ValueTask DisposeAsync()
    {
        try { await transaction.DisposeAsync(); }
        // Database rollback does not undo changes to C# objects already in memory.
        // Clearing tracked entities prevents later operations from reusing those changes.
        finally { db.ChangeTracker.Clear(); }
    }

    public static void RequireSucceeded(IdentityResult result, string operation)
    {
        // Identity can report failure without throwing. Convert unchecked failures
        // into exceptions so the surrounding transaction cannot commit partial work.
        if (!result.Succeeded)
            throw new InvalidOperationException($"Security update failed during {operation}.");
    }
}
