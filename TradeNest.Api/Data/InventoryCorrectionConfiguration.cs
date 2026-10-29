using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Data;

// Every reference includes BusinessId so a foreign tenant's valid GUID cannot be
// attached to a correction. Restrict deletion preserves the financial trail.
internal static class InventoryCorrectionConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var corrections = model.Entity<InventoryCorrection>();
        corrections.HasKey(x => new { x.BusinessId, x.Id });
        corrections.HasIndex(x => new { x.BusinessId, x.IdempotencyKey }).IsUnique();
        corrections.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
        corrections.HasOne<InventoryTransaction>().WithMany().HasForeignKey(x => new { x.BusinessId, x.TransactionId }).OnDelete(DeleteBehavior.Restrict);
        corrections.HasOne<User>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        corrections.Property(x => x.Reason).HasMaxLength(300);
        corrections.Property(x => x.IdempotencyKey).HasMaxLength(64);
        corrections.Property(x => x.RequestFingerprint).HasMaxLength(64);
        var lines = model.Entity<InventoryCorrectionLine>();
        lines.HasKey(x => new { x.BusinessId, x.Id });
        lines.HasOne<InventoryCorrection>().WithMany().HasForeignKey(x => new { x.BusinessId, x.CorrectionId }).OnDelete(DeleteBehavior.Restrict);
        lines.HasOne<InventoryTransactionLine>().WithMany().HasForeignKey(x => new { x.BusinessId, x.TransactionLineId }).OnDelete(DeleteBehavior.Restrict);
        lines.HasOne<InventoryProduct>().WithMany().HasForeignKey(x => new { x.BusinessId, x.ProductId }).OnDelete(DeleteBehavior.Restrict);
        lines.HasOne<StockMovement>().WithMany().HasForeignKey(x => new { x.BusinessId, Id = x.StockMovementId }).OnDelete(DeleteBehavior.Restrict);
        var adjustments = model.Entity<CustomerChargeAdjustment>();
        adjustments.HasKey(x => new { x.BusinessId, x.Id });
        adjustments.HasIndex(x => new { x.BusinessId, x.CorrectionId }).IsUnique();
        adjustments.HasOne<InventoryCorrection>().WithMany().HasForeignKey(x => new { x.BusinessId, x.CorrectionId }).OnDelete(DeleteBehavior.Restrict);
        adjustments.HasOne<CustomerCharge>().WithMany().HasForeignKey(x => new { x.BusinessId, x.ChargeId }).OnDelete(DeleteBehavior.Restrict);
        adjustments.ToTable(t => t.HasCheckConstraint("CK_Adjustment_Refund", "\"RefundMinor\" >= 0"));
        var dispatch = model.Entity<BusinessNotificationDispatch>();
        dispatch.HasKey(x => new { x.BusinessId, x.LocalDate });
        dispatch.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
    }
}
