using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Data;

// Composite keys make it impossible to attach another business's category/unit
// through a valid-looking GUID. SQL constraints also protect against bad callers
// that bypass normal DTO validation. Catalog archival is handled separately by the retirement migration.
internal static class InventoryConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        // BusinessId participates in every master-data FK, preventing cross-tenant links.
        var measures = model.Entity<UnitOfMeasure>();
        measures.HasKey(x => new { x.BusinessId, x.Id });
        measures.Property(x => x.Name).HasMaxLength(40);
        measures.Property(x => x.NormalizedName).HasMaxLength(40);
        measures.HasIndex(x => new { x.BusinessId, x.NormalizedName }).IsUnique();
        measures.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
        var categories = model.Entity<ProductCategory>();
        categories.HasKey(item => new { item.BusinessId, item.Id });
        categories.Property(item => item.Name).HasMaxLength(100);
        categories.Property(item => item.NormalizedName).HasMaxLength(100);
        categories.HasIndex(item => new { item.BusinessId, item.NormalizedName }).IsUnique();
        categories.HasOne<Business>().WithMany().HasForeignKey(item => item.BusinessId).OnDelete(DeleteBehavior.Restrict);

        var products = model.Entity<InventoryProduct>();
        products.HasKey(item => new { item.BusinessId, item.Id });
        products.Property(item => item.Name).HasMaxLength(160);
        products.Property(item => item.Sku).HasMaxLength(64);
        products.Property(item => item.NormalizedSku).HasMaxLength(64);
        products.Property(item => item.StockUnit).HasMaxLength(40);
        products.HasIndex(item => new { item.BusinessId, item.NormalizedSku }).IsUnique();
        products.HasOne<Business>().WithMany().HasForeignKey(item => item.BusinessId).OnDelete(DeleteBehavior.Restrict);
        products.HasOne<ProductCategory>().WithMany().HasForeignKey(item => new { item.BusinessId, item.CategoryId })
            .HasPrincipalKey(item => new { item.BusinessId, item.Id }).OnDelete(DeleteBehavior.Restrict);
        products.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => new { x.BusinessId, x.StockUnitId }).OnDelete(DeleteBehavior.Restrict);
        products.ToTable(table => table.HasCheckConstraint("CK_Product_Stock", "\"QuantityOnHandMicros\" >= 0 AND \"ReorderLevelMicros\" >= 0"));

        var units = model.Entity<ProductUnit>();
        units.HasKey(item => new { item.BusinessId, item.Id });
        units.HasAlternateKey(item => new { item.BusinessId, item.ProductId, item.Id });
        units.Property(item => item.Name).HasMaxLength(40);
        units.Property(item => item.NormalizedName).HasMaxLength(40);
        units.HasIndex(item => new { item.BusinessId, item.ProductId, item.NormalizedName }).IsUnique();
        units.HasIndex(item => new { item.BusinessId, item.ProductId }).IsUnique().HasFilter("\"IsBaseUnit\" = 1");
        units.HasOne<InventoryProduct>().WithMany().HasForeignKey(item => new { item.BusinessId, item.ProductId })
            .OnDelete(DeleteBehavior.Restrict);
        units.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => new { x.BusinessId, x.UnitOfMeasureId }).OnDelete(DeleteBehavior.Restrict);
        units.ToTable(table => table.HasCheckConstraint("CK_Unit_Factor", "\"ConversionFactorMicros\" > 0"));

        var movements = model.Entity<StockMovement>();
        movements.Property(x => x.CategoryName).HasMaxLength(100);
        movements.HasOne<ProductCategory>().WithMany().HasForeignKey(x => new { x.BusinessId, x.CategoryId }).OnDelete(DeleteBehavior.Restrict);
        movements.HasIndex(x => new { x.BusinessId, x.CategoryId, x.EffectiveDate });
        movements.HasKey(item => new { item.BusinessId, item.Id });
        movements.Property(item => item.Kind).HasMaxLength(40);
        movements.Property(item => item.UnitName).HasMaxLength(40);
        movements.Property(item => item.Reason).HasMaxLength(300);
        movements.Property(item => item.IdempotencyKey).HasMaxLength(64);
        movements.Property(item => item.RequestFingerprint).HasMaxLength(64);
        movements.HasIndex(item => new { item.BusinessId, item.IdempotencyKey }).IsUnique();
        movements.HasIndex(item => new { item.BusinessId, item.ProductId, item.RecordedAtUtc });
        movements.HasIndex(item => new { item.BusinessId, item.ProductId, item.SequenceNumber }).IsUnique();
        movements.HasOne<InventoryProduct>().WithMany().HasForeignKey(item => new { item.BusinessId, item.ProductId }).OnDelete(DeleteBehavior.Restrict);
        movements.HasOne<ProductUnit>().WithMany().HasForeignKey(item => new { item.BusinessId, item.ProductId, item.UnitId })
            .HasPrincipalKey(item => new { item.BusinessId, item.ProductId, item.Id }).OnDelete(DeleteBehavior.Restrict);
        movements.HasOne<User>().WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        // Sales use negative deltas, but balances remain nonnegative. This
        // constraint concerns the resulting balance, not the movement's sign.
        movements.ToTable(table => table.HasCheckConstraint("CK_Movement_Balance", "\"BalanceAfterMicros\" >= 0"));
    }
}
