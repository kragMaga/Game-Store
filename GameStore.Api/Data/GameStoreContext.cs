using GameStore.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Data;

public class GameStoreContext(DbContextOptions<GameStoreContext> options) : IdentityUserContext<User, int>(options)
{
    public DbSet<Game> Games => Set<Game> ();
    public DbSet<Genre> Genres => Set<Genre> ();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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

        modelBuilder.Entity<Game>()
            .HasIndex(game => new { game.GenreId, game.Price });
        modelBuilder.Entity<Game>()
            .HasIndex(game => game.Price);

        modelBuilder.Entity<Genre>().HasData(
            new { Id = 1, Name = "Fighting" },
            new { Id =2, Name = "Role Playing"},
            new { Id =3, Name = "Sports"},
            new { Id =4, Name = "Racing"},
            new { Id =5, Name = "Kids and Family"}
        );
    }
}