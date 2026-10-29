using System.Security.Claims;
using TradeNest.Api.Dtos;
using TradeNest.Api.Services;

namespace TradeNest.Api.Endpoints;

public static class BusinessEndpoints
{
    public static void MapBusinessEndpoints(this WebApplication app)
    {
        // Tenant authority is checked by BusinessAccess using current database
        // membership, not the legacy admin/customer claim in the access token.
        var businesses = app.MapGroup("/businesses").WithTags("Businesses")
            .RequireAuthorization().WithParameterValidation().AddEndpointFilter<BusinessErrorFilter>();
        businesses.MapGet("/", async (BusinessService service, ClaimsPrincipal user, int page = 1, int pageSize = 20) =>
            ValidPage(page, pageSize) ? Results.Ok(await service.ListAsync(UserId(user), page, pageSize)) : InvalidPage());
        businesses.MapPost("/", async (CreateBusinessDto request, BusinessService service, ClaimsPrincipal user) =>
        {
            var business = await service.CreateAsync(UserId(user), request);
            return Results.Created($"/businesses/{business.Id}/settings", business);
        }).RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();

        businesses.MapGet("/{businessId:guid}/settings", async (Guid businessId, BusinessService service, ClaimsPrincipal user) =>
            Results.Ok(await service.SettingsAsync(businessId, UserId(user))));
        businesses.MapPut("/{businessId:guid}/settings", async (Guid businessId, UpdateBusinessSettingsDto request,
            BusinessService service, ClaimsPrincipal user) =>
        {
            await service.UpdateSettingsAsync(businessId, UserId(user), request);
            return Results.NoContent();
        }).RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();

        businesses.MapGet("/{businessId:guid}/members", async (Guid businessId, BusinessService service,
            ClaimsPrincipal user, int page = 1, int pageSize = 20) =>
            ValidPage(page, pageSize) ? Results.Ok(await service.MembersAsync(businessId, UserId(user), page, pageSize)) : InvalidPage());
        businesses.MapPost("/{businessId:guid}/invitations", async (Guid businessId, InviteBusinessMemberDto request,
            BusinessService service, ClaimsPrincipal user) =>
        {
            await service.InviteAsync(businessId, UserId(user), request);
            return Results.Accepted(value: new { message = "Invitation queued for delivery." });
        }).RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();
        businesses.MapPut("/{businessId:guid}/members/{userId:int}", async (Guid businessId, int userId,
            UpdateBusinessMemberDto request, BusinessService service, ClaimsPrincipal user) =>
        {
            await service.UpdateMemberAsync(businessId, UserId(user), userId, request);
            return Results.NoContent();
        }).RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();

        // Acceptance needs no tenant ID from the client. The random token binds
        // the business and the confirmed recipient email; a replay is rejected.
        app.MapPost("/business-invitations/accept", async (AcceptBusinessInvitationDto request,
            BusinessService service, ClaimsPrincipal user) =>
                Results.Ok(new { businessId = await service.AcceptAsync(UserId(user), request.Token) }))
            .WithTags("Businesses").RequireAuthorization().RequireRateLimiting("auth")
            .WithParameterValidation().AddEndpointFilter<BusinessErrorFilter>();
    }

    private static int UserId(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static bool ValidPage(int page, int size) => page is >= 1 and <= 1000 && size is >= 1 and <= 100 && (long)(page - 1) * size <= 10_000;
    private static IResult InvalidPage() => Results.Problem(statusCode: 400, title: "Invalid pagination parameters");
}

// Keep expected business errors consistent without hiding unexpected persistence
// failures. No exception message from a database/email provider is returned here.
public sealed class BusinessErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (BusinessRequestException exception)
        { return Results.Problem(statusCode: exception.StatusCode, title: exception.Message); }
    }
}
