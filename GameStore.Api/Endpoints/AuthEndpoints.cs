using System.Security.Claims;
using GameStore.Api.Dtos;
using GameStore.Api.Services;

namespace GameStore.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/register", async (RegisterDto request, AuthService authService) =>
{
            var result = await authService.RegisterAsync(request);

            return result switch
            {
                RegistrationError.None => Results.Ok(new { message = "User registered successfully." }),
                RegistrationError.UsernameRequired => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Username is required",
                    detail: "Provide a username."),
                RegistrationError.UsernameLengthInvalid => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid username",
                    detail: "Username must be between 5 and 20 characters."),
                RegistrationError.PasswordRequired => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Password is required",
                    detail: "Provide a password containing at least 8 characters."),
                RegistrationError.PasswordTooShort => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Password is too short",
                    detail: "Password must contain at least 8 characters."),
                RegistrationError.UsernameAlreadyTaken => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Username is already taken",
                    detail: "Choose a different username."),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Registration failed",
                    detail: "The registration could not be completed.")
            };
})
    .WithTags("Authentication")
    .WithSummary("Register an account")
    .WithDescription("Registers a customer account. Username conflicts return 409 ProblemDetails.");

        app.MapPost("/auth/login", async (LoginDto request, AuthService authService) =>
{
    var token = await authService.LoginAsync(request);

    return token is null
        ? Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Authentication failed",
            detail: "The username or password is incorrect.")
        : Results.Ok(new { token });
})
    .WithTags("Authentication")
    .WithSummary("Log in")
    .WithDescription("Valid credentials return a JWT access token.");

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
    .WithTags("Authentication")
    .WithSummary("Get the current user")
    .RequireAuthorization();

        app.MapGet("/auth/users", async (UserService userService) =>
{
    var users = await userService.GetUsersAsync();

    return Results.Ok(users);
})
    .WithTags("User administration")
    .WithSummary("List users")
    .RequireAuthorization("CanManageUsers");


 app.MapPut("/auth/users/{id}/role", async (
    int id,
    UpdateUserRoleDto request,
    UserService userService) =>
{
    var error = await userService.UpdateRoleAsync(
        id,
        request);

    return error switch
{
    UserOperationError.None => Results.NoContent(),

    UserOperationError.InvalidRole => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid role",
        detail: "Role must be either admin or customer."),

    UserOperationError.UserNotFound => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "User not found",
        detail: "The requested user does not exist."),

    UserOperationError.LastAdmin => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Operation not allowed",
        detail: "The last admin cannot be demoted."),

    _ => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Operation failed",
        detail: "The operation could not be completed.")
};
})
    .WithTags("User administration")
    .WithSummary("Change a user's role")
    .WithDescription("Assigns the admin or customer role. The last administrator cannot be demoted.")
    .RequireAuthorization("CanManageUsers");


app.MapDelete("/auth/users/{id}", async (int id, ClaimsPrincipal currentUser, UserService userService) =>
{
    var error = await userService.DeleteUserAsync(id, currentUser);

    return error switch
{
    UserOperationError.None => Results.NoContent(),

    UserOperationError.UserNotFound => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "User not found",
        detail: "The requested user does not exist."),

    UserOperationError.CannotDeleteSelf => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Operation not allowed",
        detail: "You cannot delete your own account."),

    UserOperationError.LastAdmin => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Operation not allowed",
        detail: "The last admin cannot be deleted."),

    _ => Results.Problem(
        statusCode: StatusCodes.Status500InternalServerError,
        title: "Operation failed",
        detail: "The operation could not be completed.")
};
})
    .WithTags("User administration")
    .WithSummary("Delete a user")
    .WithDescription("Deletes a user unless that would delete the current account or the last administrator.")
    .RequireAuthorization("CanManageUsers");


    }
}