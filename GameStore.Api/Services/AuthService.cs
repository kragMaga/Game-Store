using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace GameStore.Api.Services;

public class AuthService(GameStoreContext dbContext, IConfiguration configuration)
{
    public async Task<bool> RegisterAsync(RegisterDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return false;
        }

        if (request.Password.Length < 8)
        {
            return false;
        }

        var username = request.Username.Trim();

        var userAlreadyExists = await dbContext.Users
            .AnyAsync(user => user.Username == username);

        if (userAlreadyExists)
        {
            return false;
        }

        var user = new User
        {
            Username = username,
            Role = "customer",
            PasswordHash = ""
        };

        var passwordHasher = new PasswordHasher<User>();

        user.PasswordHash = passwordHasher.HashPassword(
            user,
            request.Password);

        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();

        return true;
    }

    public async Task<string?> LoginAsync(LoginDto request)
    {
        var user = await dbContext.Users
            .SingleOrDefaultAsync(
                user => user.Username == request.Username);

        if (user is null)
        {
            return null;
        }

        var passwordHasher = new PasswordHasher<User>();

        var passwordResult =
            passwordHasher.VerifyHashedPassword(
                user,
                user.PasswordHash,
                request.Password);

        if (passwordResult == PasswordVerificationResult.Failed)
        {
            return null;
        }

        return CreateToken(user);
    }

    private string CreateToken(User user)
    {
        var jwtKey = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException(
                "JWT key is missing.");

        var jwtIssuer = configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException(
                "JWT issuer is missing.");

        var jwtAudience = configuration["Jwt:Audience"]
            ?? throw new InvalidOperationException(
                "JWT audience is missing.");

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        };

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(jwtKey));

        var credentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler()
            .WriteToken(token);
    }
}