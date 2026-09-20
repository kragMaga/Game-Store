using System.Text;
using GameStore.Api.Data;
using GameStore.Api.Endpoints;
using GameStore.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connString = builder.Configuration.GetConnectionString("GameStore");
builder.Services.AddSqlite<GameStoreContext>(connString);
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserService>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT key is missing.");

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("JWT issuer is missing.");

var jwtAudience = builder.Configuration["Jwt:Audience"] ?? throw new InvalidOperationException("JWT audience is missing.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,

            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CanManageGames", policy =>
    {
        policy.RequireRole("admin");
    });

    options.AddPolicy("CanManageUsers", policy =>
    {
        policy.RequireRole("admin");
    });

    options.AddPolicy("CanManageGenres", policy =>
    {
        policy.RequireRole("admin");
    });
});

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

app.MapGamesEndpoints();
app.MapGenresEndpoints();
app.MapAuthEndpoints();

await app.MigrateDbAsync();
app.Run();