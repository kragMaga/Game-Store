using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TradeNest.Api.Data;

// Composite membership keys prevent duplicate membership in one business while
// permitting the same account to participate in many separate businesses.
internal sealed class BusinessConfiguration : IEntityTypeConfiguration<Business>
{
    public void Configure(EntityTypeBuilder<Business> entity)
    {
        entity.Property(item => item.Name).HasMaxLength(120);
        entity.Property(item => item.Currency).HasMaxLength(3);
        // Zero is a valid precision, not an "unset" value. The negative sentinel
        // prevents EF from replacing an explicitly chosen zero with the DB default.
        entity.Property(item => item.CurrencyDecimalPlaces).HasDefaultValue(2).HasSentinel(-1);
        entity.Property(item => item.NotificationEmail).HasMaxLength(254);
        entity.Property(item => item.TimeZoneId).HasMaxLength(100);
        // Account deletion must never silently delete a business and its history.
        entity.HasOne<User>().WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BusinessMembershipConfiguration : IEntityTypeConfiguration<BusinessMembership>
{
    public void Configure(EntityTypeBuilder<BusinessMembership> entity)
    {
        entity.HasKey(item => new { item.BusinessId, item.UserId });
        entity.Property(item => item.Role).HasConversion<string>().HasMaxLength(20);
        entity.HasIndex(item => item.UserId);
        // A filtered unique index protects against accidentally creating a second
        // owner. The service also forbids demoting/deactivating the existing owner.
        entity.HasIndex(item => item.BusinessId).IsUnique().HasFilter("\"Role\" = 'Owner'");
        entity.HasOne<Business>().WithMany().HasForeignKey(item => item.BusinessId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BusinessInvitationConfiguration : IEntityTypeConfiguration<BusinessInvitation>
{
    public void Configure(EntityTypeBuilder<BusinessInvitation> entity)
    {
        entity.Property(item => item.NormalizedEmail).HasMaxLength(254);
        entity.Property(item => item.TokenHash).HasMaxLength(64);
        entity.Property(item => item.Role).HasConversion<string>().HasMaxLength(20);
        entity.HasIndex(item => item.TokenHash).IsUnique();
        entity.HasIndex(item => new { item.BusinessId, item.NormalizedEmail });
        entity.HasOne<Business>().WithMany().HasForeignKey(item => item.BusinessId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>().WithMany().HasForeignKey(item => item.InvitedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BusinessAuditConfiguration : IEntityTypeConfiguration<BusinessAuditEvent>
{
    public void Configure(EntityTypeBuilder<BusinessAuditEvent> entity)
    {
        entity.Property(item => item.Action).HasMaxLength(80);
        entity.HasIndex(item => new { item.BusinessId, item.CreatedAtUtc });
        entity.HasOne<Business>().WithMany().HasForeignKey(item => item.BusinessId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<User>().WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
