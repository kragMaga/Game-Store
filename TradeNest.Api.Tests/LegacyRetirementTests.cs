using System.IdentityModel.Tokens.Jwt;
using TradeNest.Api.Data;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class LegacyRetirementTests
{
    [Fact]
    public async Task Startup_needs_no_bootstrap_user_and_normal_accounts_get_authority_only_through_membership()
    {
        using var factory = new TradeNestWebApplicationFactory(seedTestAccount: false);
        using var scope = factory.Services.CreateScope(); var p = scope.ServiceProvider;
        var db = p.GetRequiredService<TradeNestContext>(); Assert.Empty(await db.Users.ToListAsync());
        var user = new User { UserName = "first.owner", Email = "first@example.test", EmailConfirmed = true };
        Assert.True((await p.GetRequiredService<UserManager<User>>().CreateAsync(user, "Cedar-Moon-River-593!")).Succeeded);
        var login = await p.GetRequiredService<AuthService>().LoginAsync(new("first.owner", "Cedar-Moon-River-593!"));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login!.Tokens!.AccessToken);
        Assert.DoesNotContain(jwt.Claims, c => c.Type is "role" or "roles" || c.Type == System.Security.Claims.ClaimTypes.Role);
        var business = await p.GetRequiredService<BusinessService>().CreateAsync(user.Id, new("First business", "NGN", "first@example.test", "Africa/Lagos"));
        Assert.Equal(BusinessRole.Owner, (await p.GetRequiredService<BusinessAccess>().RequireAsync(business.Id, user.Id, true)).Role);
        Assert.Null(db.Model.FindEntityType(typeof(User))!.FindProperty("Role"));
    }

    [Fact]
    public async Task Removal_deletes_legacy_archives_without_changing_accounts_or_tenant_records()
    {
        using var factory = new TradeNestWebApplicationFactory(); using var scope = factory.Services.CreateScope(); var p = scope.ServiceProvider;
        var db = p.GetRequiredService<TradeNestContext>(); var user = await db.Users.AsNoTracking().SingleAsync();
        var business = await p.GetRequiredService<BusinessService>().CreateAsync(user.Id, new("Preserved business", "NGN", "owner@example.test", "Africa/Lagos"));
        var migrator = db.GetService<IMigrator>(); db.ChangeTracker.Clear();
        await migrator.MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("_AddDocumentNumbersAndUserWorkflows")));
        // Downgrading restores only empty legacy schemas, so insert explicit test data.
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Genres (Id, Name) VALUES (900, 'Legacy genre')");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Games (Id, Name, GenreId, Price, ReleasedDate) VALUES (900, 'Preserved catalog row', 900, '12.50', '2026-01-01')");
        await db.Database.ExecuteSqlRawAsync("UPDATE Users SET Role = 'admin'");
        // Exercise the intermediate archive step and then the destructive cleanup.
        await migrator.MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("_RetireLegacyCatalogAndGlobalRoles")));
        Assert.Equal("Preserved catalog row", await db.Database.SqlQueryRaw<string>("SELECT Name AS Value FROM ArchivedGames WHERE Id = 900").SingleAsync());
        await migrator.MigrateAsync();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE name IN ('Games','Genres','GameSearch','GenreSearch','ArchivedGames','ArchivedGenres','ArchivedGlobalRoles')").SingleAsync());
        Assert.Equal(user.PasswordHash, (await db.Users.AsNoTracking().SingleAsync()).PasswordHash);
        Assert.Equal(business.Id, (await db.Businesses.SingleAsync()).Id);
        Assert.Equal(BusinessRole.Owner, (await p.GetRequiredService<BusinessAccess>().RequireAsync(business.Id, user.Id, true)).Role);
        Assert.Null(db.Model.FindEntityType("TradeNest.Api.Entities.Game"));
    }
}
