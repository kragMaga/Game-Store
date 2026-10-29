using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Data;

// Tenant-qualified foreign keys protect every customer/charge/payment relationship.
// Business rules also verify that allocations belong to one specific customer.
internal static class CustomerLedgerConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var customers = model.Entity<Customer>();
        customers.HasKey(item => new { item.BusinessId, item.Id });
        customers.Property(item => item.Code).HasMaxLength(64);
        customers.Property(item => item.NormalizedCode).HasMaxLength(64);
        customers.Property(item => item.Name).HasMaxLength(160);
        customers.Property(item => item.Email).HasMaxLength(254);
        customers.Property(item => item.Phone).HasMaxLength(50);
        customers.HasIndex(item => new { item.BusinessId, item.NormalizedCode }).IsUnique();
        customers.HasOne<Business>().WithMany().HasForeignKey(item => item.BusinessId).OnDelete(DeleteBehavior.Restrict);

        var charges = model.Entity<CustomerCharge>();
        charges.HasKey(item => new { item.BusinessId, item.Id });
        charges.Property(item => item.Kind).HasMaxLength(30);
        charges.Property(item => item.Currency).HasMaxLength(3);
        charges.Property(item => item.Reason).HasMaxLength(300);
        charges.Property(item => item.IdempotencyKey).HasMaxLength(64);
        charges.Property(item => item.RequestFingerprint).HasMaxLength(64);
        charges.HasIndex(item => new { item.BusinessId, item.IdempotencyKey }).IsUnique();
        charges.HasIndex(item => new { item.BusinessId, item.SaleId }).IsUnique().HasFilter("\"SaleId\" IS NOT NULL");
        charges.HasIndex(item => new { item.BusinessId, item.CustomerId }).IsUnique().HasFilter("\"Kind\" = 'OpeningDebt'");
        charges.HasOne<Customer>().WithMany().HasForeignKey(item => new { item.BusinessId, item.CustomerId }).HasPrincipalKey(item => new { item.BusinessId, item.Id }).OnDelete(DeleteBehavior.Restrict);
        charges.HasOne<InventoryTransaction>().WithMany().HasForeignKey(item => new { item.BusinessId, item.SaleId }).HasPrincipalKey(item => new { item.BusinessId, item.Id }).OnDelete(DeleteBehavior.Restrict);
        charges.HasOne<User>().WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        charges.ToTable(table => table.HasCheckConstraint("CK_Charge_Amount", "\"AmountMinor\" >= 0 AND \"CurrencyDecimalPlaces\" BETWEEN 0 AND 4"));

        var payments = model.Entity<CustomerPayment>();
        payments.HasKey(item => new { item.BusinessId, item.Id });
        payments.Property(item => item.Kind).HasMaxLength(30);
        payments.Property(item => item.Method).HasMaxLength(30);
        payments.Property(item => item.Currency).HasMaxLength(3);
        payments.Property(item => item.Reference).HasMaxLength(100);
        payments.Property(item => item.IdempotencyKey).HasMaxLength(64);
        payments.Property(item => item.RequestFingerprint).HasMaxLength(64);
        payments.HasIndex(item => new { item.BusinessId, item.IdempotencyKey }).IsUnique();
        payments.HasIndex(item => new { item.BusinessId, item.CustomerId, item.RecordedAtUtc });
        payments.HasOne<Customer>().WithMany().HasForeignKey(item => new { item.BusinessId, item.CustomerId }).HasPrincipalKey(item => new { item.BusinessId, item.Id }).OnDelete(DeleteBehavior.Restrict);
        payments.HasOne<User>().WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        payments.ToTable(table => table.HasCheckConstraint("CK_Payment_Amount", "\"AmountMinor\" > 0 AND \"CurrencyDecimalPlaces\" BETWEEN 0 AND 4"));

        var allocations = model.Entity<CustomerPaymentAllocation>();
        allocations.HasKey(item => new { item.BusinessId, item.PaymentId, item.ChargeId });
        allocations.HasOne<CustomerPayment>().WithMany().HasForeignKey(item => new { item.BusinessId, item.PaymentId }).OnDelete(DeleteBehavior.Restrict);
        allocations.HasOne<CustomerCharge>().WithMany().HasForeignKey(item => new { item.BusinessId, item.ChargeId }).OnDelete(DeleteBehavior.Restrict);
        allocations.ToTable(table => table.HasCheckConstraint("CK_Allocation_Amount", "\"AmountMinor\" > 0"));

        var reversals = model.Entity<CustomerPaymentReversal>();
        reversals.HasKey(item => new { item.BusinessId, item.Id });
        reversals.Property(item => item.Reason).HasMaxLength(300);
        reversals.Property(item => item.IdempotencyKey).HasMaxLength(64);
        reversals.Property(item => item.RequestFingerprint).HasMaxLength(64);
        reversals.HasIndex(item => new { item.BusinessId, item.PaymentId }).IsUnique();
        reversals.HasIndex(item => new { item.BusinessId, item.IdempotencyKey }).IsUnique();
        reversals.HasOne<CustomerPayment>().WithMany().HasForeignKey(item => new { item.BusinessId, item.PaymentId }).OnDelete(DeleteBehavior.Restrict);
        reversals.HasOne<User>().WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);

        var sales = model.Entity<InventoryTransaction>();
        sales.Property(item => item.CustomerName).HasMaxLength(160);
        sales.HasOne<Customer>().WithMany().HasForeignKey(item => new { item.BusinessId, item.CustomerId })
            .HasPrincipalKey(item => new { item.BusinessId, item.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
