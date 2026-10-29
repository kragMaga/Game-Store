using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Data;

// Financial records and their stock effects share tenant-qualified foreign keys.
// Restrict deletion so completed transaction history cannot disappear by cascade.
internal static class InventoryTransactionConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var suppliers = model.Entity<Supplier>();
        suppliers.HasKey(item => new { item.BusinessId, item.Id });
        suppliers.Property(item => item.Name).HasMaxLength(160);
        suppliers.Property(item => item.NormalizedName).HasMaxLength(160);
        suppliers.Property(item => item.ContactReference).HasMaxLength(150);
        suppliers.HasIndex(item => new { item.BusinessId, item.NormalizedName }).IsUnique();
        suppliers.HasOne<Business>().WithMany().HasForeignKey(item => item.BusinessId).OnDelete(DeleteBehavior.Restrict);

        var transactions = model.Entity<InventoryTransaction>();
        transactions.HasKey(item => new { item.BusinessId, item.Id });
        transactions.Property(item => item.Kind).HasMaxLength(20);
        transactions.Property(item => item.Currency).HasMaxLength(3);
        transactions.Property(item => item.Reference).HasMaxLength(100);
        transactions.Property(item => item.IdempotencyKey).HasMaxLength(64);
        transactions.Property(item => item.RequestFingerprint).HasMaxLength(64);
        transactions.HasIndex(item => new { item.BusinessId, item.IdempotencyKey }).IsUnique();
        transactions.HasIndex(item => new { item.BusinessId, item.Kind, item.RecordedAtUtc });
        transactions.HasOne<Business>().WithMany().HasForeignKey(item => item.BusinessId).OnDelete(DeleteBehavior.Restrict);
        transactions.HasOne<Supplier>().WithMany().HasForeignKey(item => new { item.BusinessId, item.SupplierId })
            .HasPrincipalKey(item => new { item.BusinessId, item.Id }).OnDelete(DeleteBehavior.Restrict);
        transactions.HasOne<User>().WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        transactions.ToTable(table => table.HasCheckConstraint("CK_Transaction_Total", "\"TotalMinor\" >= 0 AND \"CurrencyDecimalPlaces\" BETWEEN 0 AND 4"));

        var lines = model.Entity<InventoryTransactionLine>();
        lines.HasKey(item => new { item.BusinessId, item.Id });
        lines.Property(item => item.ProductName).HasMaxLength(160);
        lines.Property(item => item.Sku).HasMaxLength(64);
        lines.Property(item => item.UnitName).HasMaxLength(40);
        lines.HasIndex(item => new { item.BusinessId, item.TransactionId, item.Position }).IsUnique();
        lines.HasIndex(item => new { item.BusinessId, item.StockMovementId }).IsUnique();
        lines.HasOne<InventoryTransaction>().WithMany().HasForeignKey(item => new { item.BusinessId, item.TransactionId }).OnDelete(DeleteBehavior.Restrict);
        lines.HasOne<InventoryProduct>().WithMany().HasForeignKey(item => new { item.BusinessId, item.ProductId }).OnDelete(DeleteBehavior.Restrict);
        lines.HasOne<ProductUnit>().WithMany().HasForeignKey(item => new { item.BusinessId, item.ProductId, item.UnitId })
            .HasPrincipalKey(item => new { item.BusinessId, item.ProductId, item.Id }).OnDelete(DeleteBehavior.Restrict);
        lines.HasOne<StockMovement>().WithMany().HasForeignKey(item => new { item.BusinessId, item.StockMovementId }).OnDelete(DeleteBehavior.Restrict);
        lines.ToTable(table => table.HasCheckConstraint("CK_TransactionLine_Amounts", "\"QuantityMicros\" > 0 AND \"BaseQuantityMicros\" > 0 AND \"UnitPriceMicros\" >= 0 AND \"LineTotalMinor\" >= 0"));
    }
}
