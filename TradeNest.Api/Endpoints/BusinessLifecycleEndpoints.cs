using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using TradeNest.Api.Services;
namespace TradeNest.Api.Endpoints;

// Explicit confirmation prevents a stray click from closing a business. Sensitive
// operations also require recent credentials and service-level ownership checks.
public sealed record TransferBusinessDto(int NewOwnerUserId);
public sealed record CloseBusinessDto([Required, StringLength(160)] string ConfirmBusinessName);
public static class BusinessLifecycleEndpoints
{
    public static void MapBusinessLifecycleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/businesses/{businessId:guid}").RequireAuthorization().WithTags("Business lifecycle")
            .WithParameterValidation().AddEndpointFilter<BusinessErrorFilter>();
        group.MapGet("/audit-events", async (Guid businessId, ClaimsPrincipal user, BusinessLifecycleService service, int page = 1, int pageSize = 20) =>
            Results.Ok(await service.AuditAsync(businessId, Id(user), page, pageSize)));
        group.MapGet("/invitations", async (Guid businessId, ClaimsPrincipal user, BusinessLifecycleService service, int page = 1, int pageSize = 20) =>
            Results.Ok(await service.InvitationsAsync(businessId, Id(user), page, pageSize)));
        Secure(group.MapDelete("/invitations/{invitationId:guid}", async (Guid businessId, Guid invitationId, ClaimsPrincipal user, BusinessLifecycleService service) =>
        { await service.CancelAsync(businessId, invitationId, Id(user)); return Results.NoContent(); }));
        Secure(group.MapPost("/transfer-ownership", async (Guid businessId, TransferBusinessDto request, ClaimsPrincipal user, BusinessLifecycleService service) =>
        { await service.TransferAsync(businessId, Id(user), request.NewOwnerUserId); return Results.NoContent(); }));
        foreach (var reopen in new[] { false, true })
        {
            var open = reopen;
            Secure(group.MapPost(open ? "/reopen" : "/close", async (Guid businessId, CloseBusinessDto request, ClaimsPrincipal user, BusinessLifecycleService service) =>
            { await service.CloseAsync(businessId, Id(user), request.ConfirmBusinessName, open); return Results.NoContent(); }));
        }
        Secure(app.MapPost("/auth/close-account", async (ClaimsPrincipal user, BusinessLifecycleService service) =>
        { await service.CloseAccountAsync(Id(user)); return Results.NoContent(); }).RequireAuthorization().AddEndpointFilter<BusinessErrorFilter>());
    }
    private static void Secure(RouteHandlerBuilder route) => route.RequireRateLimiting("auth").AddEndpointFilter<RecentAuthenticationFilter>();
    private static int Id(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
