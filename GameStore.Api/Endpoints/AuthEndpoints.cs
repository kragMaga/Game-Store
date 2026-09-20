using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using GameStore.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace GameStore.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/register", async (RegisterDto request, AuthService authService) =>
{
    var success = await authService.RegisterAsync(request);

    return success
        ? Results.Ok("User registered successfully.")
        : Results.BadRequest("Unable to register user.");
});

        app.MapPost("/auth/login", async (LoginDto request, AuthService authService) =>
{
    var token = await authService.LoginAsync(request);

    return token is null
        ? Results.Unauthorized()
        : Results.Ok(new { token });
});

        app.MapGet("/auth/me", (ClaimsPrincipal user) =>
{
    return Results.Ok(new
    {
        Username = user.Identity?.Name,
        IsAuthenticated = user.Identity?.IsAuthenticated,
        Roles = user.Claims
            .Where(claim => claim.Type == ClaimTypes.Role)
            .Select(claim => claim.Value)
    });
})
.RequireAuthorization();

        app.MapGet("/auth/users", async (UserService userService) =>
{
    var users = await userService.GetUsersAsync();

    return Results.Ok(users);
})
.RequireAuthorization("CanManageUsers");


 app.MapPut("/auth/users/{id}/role", async (
    int id,
    UpdateUserRoleDto request,
    UserService userService,
    ClaimsPrincipal currentUser) =>
{
    var error = await userService.UpdateRoleAsync(
        id,
        request);

    return error switch
{
    UserOperationError.None => Results.NoContent(),

    UserOperationError.InvalidRole => Results.BadRequest(
        new ErrorDto(
            "InvalidRole",
            "Role must be either admin or customer.")),

    UserOperationError.UserNotFound => Results.NotFound(
        new ErrorDto(
            "UserNotFound",
            "The requested user does not exist.")),

    UserOperationError.LastAdmin => Results.BadRequest(
        new ErrorDto(
            "LastAdmin",
            "The last admin cannot be demoted.")),

    _ => Results.BadRequest(
        new ErrorDto(
            "UnknownError",
            "The operation could not be completed."))
};
})
.RequireAuthorization("CanManageUsers");


app.MapDelete("/auth/DeleteUser/{id}", async (int id, ClaimsPrincipal currentUser, UserService userService) =>
{
    var error = await userService.DeleteUserAsync(id, currentUser);

    return error switch
{
    UserOperationError.None => Results.NoContent(),

    UserOperationError.UserNotFound => Results.NotFound(
        new ErrorDto(
            "UserNotFound",
            "The requested user does not exist.")),

    UserOperationError.CannotDeleteSelf => Results.BadRequest(
        new ErrorDto(
            "CannotDeleteSelf",
            "You cannot delete your own account.")),

    UserOperationError.LastAdmin => Results.BadRequest(
        new ErrorDto(
            "LastAdmin",
            "The last admin cannot be deleted.")),

    _ => Results.BadRequest(
        new ErrorDto(
            "UnknownError",
            "The operation could not be completed."))
};
})
.RequireAuthorization("CanManageUsers");


    }
}