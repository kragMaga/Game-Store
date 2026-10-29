using System.Globalization;
using TradeNest.Api.Data;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;
namespace TradeNest.Api.Services;

internal static class DocumentNumbers
{
    public static async Task<string> NextAsync(TradeNestContext db, Guid businessId, string prefix)
    {
        // The SQLite writer lock serializes both the counter and document insert.
        // Rollback (including a preview) restores the counter; retries allocate none.
        if (db.Database.CurrentTransaction is null || db.InventoryBusinessId != businessId)
            throw new InvalidOperationException("Document numbers require the verified tenant posting transaction.");
        var counter = db.DocumentSequences.Local.SingleOrDefault(c => c.BusinessId == businessId && c.Prefix == prefix)
            ?? await db.DocumentSequences.SingleOrDefaultAsync(c => c.BusinessId == businessId && c.Prefix == prefix);
        if (counter is null) { counter = new() { BusinessId = businessId, Prefix = prefix }; db.DocumentSequences.Add(counter); }
        if (counter.LastNumber >= 9_999_999_999) throw new BusinessRequestException(409, "Document sequence limit reached");
        return prefix + "-" + (++counter.LastNumber).ToString("D6", CultureInfo.InvariantCulture);
    }
}
