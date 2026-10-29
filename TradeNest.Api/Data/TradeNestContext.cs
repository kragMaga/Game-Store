using TradeNest.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Data;

public class TradeNestContext(DbContextOptions<TradeNestContext> options) : IdentityUserContext<User, int>(options)
{
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<BusinessMembership> BusinessMemberships => Set<BusinessMembership>();
    public DbSet<BusinessInvitation> BusinessInvitations => Set<BusinessInvitation>();
    public DbSet<BusinessAuditEvent> BusinessAuditEvents => Set<BusinessAuditEvent>();
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();
    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();
    public DbSet<InventoryProduct> InventoryProducts => Set<InventoryProduct>();
    public DbSet<ProductUnit> ProductUnits => Set<ProductUnit>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();
    public DbSet<InventoryTransactionLine> InventoryTransactionLines => Set<InventoryTransactionLine>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerCharge> CustomerCharges => Set<CustomerCharge>();
    public DbSet<CustomerPayment> CustomerPayments => Set<CustomerPayment>();
    public DbSet<CustomerPaymentAllocation> CustomerPaymentAllocations => Set<CustomerPaymentAllocation>();
    public DbSet<CustomerPaymentReversal> CustomerPaymentReversals => Set<CustomerPaymentReversal>();

    public DbSet<InventoryCorrection> InventoryCorrections => Set<InventoryCorrection>();
    public DbSet<InventoryCorrectionLine> InventoryCorrectionLines => Set<InventoryCorrectionLine>();
    public DbSet<CustomerChargeAdjustment> CustomerChargeAdjustments => Set<CustomerChargeAdjustment>();
    public DbSet<BusinessNotificationDispatch> BusinessNotificationDispatches => Set<BusinessNotificationDispatch>();

    public DbSet<BusinessNotification> BusinessNotifications => Set<BusinessNotification>();
    public DbSet<DebtReminderDispatch> DebtReminderDispatches => Set<DebtReminderDispatch>();
    public DbSet<InventoryImportBatch> InventoryImportBatches => Set<InventoryImportBatch>();

    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();

    // Fail closed by default: inventory queries return no rows until a service
    // has verified live membership and selected the request's business scope.
    public Guid? InventoryBusinessId { get; internal set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        InventoryConfiguration.Configure(modelBuilder);
        InventoryTransactionConfiguration.Configure(modelBuilder);
        CustomerLedgerConfiguration.Configure(modelBuilder);
        InventoryCorrectionConfiguration.Configure(modelBuilder);
        // SQL uniqueness complements serialized allocation and protects imports or
        // future services from accidentally reusing a readable reference.
        var sequence = modelBuilder.Entity<DocumentSequence>();
        sequence.HasKey(x => new { x.BusinessId, x.Prefix });
        sequence.HasQueryFilter(x => x.BusinessId == InventoryBusinessId);
        sequence.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransaction>().HasIndex(x => new { x.BusinessId, x.DocumentNumber }).IsUnique();
        modelBuilder.Entity<CustomerPayment>().HasIndex(x => new { x.BusinessId, x.DocumentNumber }).IsUnique();
        modelBuilder.Entity<CustomerPaymentReversal>().HasIndex(x => new { x.BusinessId, x.DocumentNumber }).IsUnique();
        modelBuilder.Entity<InventoryCorrection>().HasIndex(x => new { x.BusinessId, x.DocumentNumber }).IsUnique();
        // Operational metadata follows the same default-deny tenant boundary.
        var notification = modelBuilder.Entity<BusinessNotification>();
        notification.HasKey(x => new { x.BusinessId, x.Id });
        notification.HasIndex(x => x.OutboxMessageId).IsUnique();
        notification.HasIndex(x => new { x.BusinessId, x.Kind, x.LocalDate, x.Recipient }).IsUnique();
        notification.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
        notification.HasQueryFilter(x => x.BusinessId == InventoryBusinessId);
        var dispatch = modelBuilder.Entity<DebtReminderDispatch>();
        dispatch.HasKey(x => new { x.BusinessId, x.LocalDate });
        dispatch.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
        dispatch.HasQueryFilter(x => x.BusinessId == InventoryBusinessId);
        var import = modelBuilder.Entity<InventoryImportBatch>();
        import.HasKey(x => new { x.BusinessId, x.Id });
        import.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
        import.HasOne<User>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        import.HasQueryFilter(x => x.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<InventoryCorrection>().HasQueryFilter(x => x.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<InventoryCorrectionLine>().HasQueryFilter(x => x.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<CustomerChargeAdjustment>().HasQueryFilter(x => x.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<BusinessNotificationDispatch>().HasQueryFilter(x => x.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<Customer>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<CustomerCharge>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<CustomerPayment>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<CustomerPaymentAllocation>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<CustomerPaymentReversal>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<Supplier>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<InventoryTransaction>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<InventoryTransactionLine>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<UnitOfMeasure>().HasQueryFilter(x => x.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<ProductCategory>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<InventoryProduct>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<ProductUnit>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        modelBuilder.Entity<StockMovement>().HasQueryFilter(item => item.BusinessId == InventoryBusinessId);
        // Keep tenant model configuration separate from Identity mappings
        // so future inventory entities have an obvious home and isolation boundary.
        modelBuilder.ApplyConfiguration(new BusinessConfiguration());
        modelBuilder.ApplyConfiguration(new BusinessMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new BusinessInvitationConfiguration());
        modelBuilder.ApplyConfiguration(new BusinessAuditConfiguration());
        modelBuilder.Entity<EmailOutboxMessage>().HasKey(item => item.Id);
        modelBuilder.Entity<EmailOutboxMessage>().HasIndex(item => new { item.Status, item.NextAttemptAtUtc, item.LeaseExpiresAtUtc });

        modelBuilder.Entity<User>().ToTable("Users");
        modelBuilder.Entity<User>().Property(user => user.UserName).HasColumnName("Username");
        modelBuilder.Entity<IdentityUserClaim<int>>().ToTable("UserClaims");
        modelBuilder.Entity<IdentityUserLogin<int>>().ToTable("UserLogins");
        modelBuilder.Entity<IdentityUserToken<int>>().ToTable("UserTokens");
        modelBuilder.Entity<User>()
            .HasIndex(user => user.NormalizedUserName)
            .IsUnique();
        modelBuilder.Entity<User>()
            .HasIndex(user => user.NormalizedEmail)
            .IsUnique();

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.SessionId, token.ExpiresAtUtc });
            entity.HasIndex(token => new { token.UserId, token.ExpiresAtUtc });
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(token => token.SessionId).HasMaxLength(32).IsRequired();
            entity.HasIndex(token => new { token.UserId, token.SessionId, token.RevokedAtUtc });
        });

        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.HasKey(session => session.Id);
            entity.Property(session => session.Id).HasMaxLength(32);
            entity.HasIndex(session => new { session.UserId, session.RevokedAtUtc });
            entity.HasOne(session => session.User)
                .WithMany()
                .HasForeignKey(session => session.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });


    }

    // Query filters do not protect writes. Validate tracked inventory changes too,
    // and make movement history append-only through normal EF persistence paths.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateInventoryWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateInventoryWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateInventoryWrites()
    {
        // Authority and operation evidence must survive later edits/closure too.
        // Audits are not tenant-filtered because onboarding spans businesses, so
        // their services apply explicit tenant guards before inserting them.
        if (ChangeTracker.Entries<BusinessAuditEvent>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Business audit history is append-only.");
        foreach (var entry in ChangeTracker.Entries<IInventoryRow>().Where(entry =>
                     entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (InventoryBusinessId is null || entry.Entity.BusinessId != InventoryBusinessId ||
                entry.State != EntityState.Added && entry.OriginalValues.GetValue<Guid>(nameof(IInventoryRow.BusinessId)) != InventoryBusinessId)
                throw new InvalidOperationException("Inventory write does not match the verified business scope.");
            // A base-unit change would reinterpret both saved balances and every
            // category report. Create a new product instead of changing its meaning.
            if (entry.Entity is InventoryProduct && entry.State == EntityState.Modified &&
                (entry.Property(nameof(InventoryProduct.StockUnitId)).IsModified ||
                 entry.Property(nameof(InventoryProduct.StockUnit)).IsModified ||
                 entry.Property(nameof(InventoryProduct.AllowsFractionalStock)).IsModified))
                throw new InvalidOperationException("A product's base unit cannot be changed.");
            if (entry.Entity is StockMovement && entry.State != EntityState.Added)
                throw new InvalidOperationException("Stock movement history cannot be edited or deleted.");
            if (entry.Entity is ProductUnit && entry.State != EntityState.Added)
                throw new InvalidOperationException("Unit conversions cannot be edited or deleted; add a new named unit instead.");
            if (entry.Entity is InventoryTransaction or InventoryTransactionLine && entry.State != EntityState.Added)
                throw new InvalidOperationException("Completed transactions cannot be edited or deleted; use a controlled reversal.");
            if (entry.Entity is InventoryImportBatch or DebtReminderDispatch or InventoryCorrection or InventoryCorrectionLine or CustomerChargeAdjustment or BusinessNotificationDispatch && entry.State != EntityState.Added)
                throw new InvalidOperationException("Correction and dispatch history is append-only.");
            if (entry.Entity is CustomerCharge or CustomerPayment or CustomerPaymentAllocation or CustomerPaymentReversal && entry.State != EntityState.Added)
                throw new InvalidOperationException("Customer ledger history is append-only; use an explicit correction event.");
        }
    }
}
