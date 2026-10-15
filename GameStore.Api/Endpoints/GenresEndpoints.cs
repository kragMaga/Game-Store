using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Mapping;
using GameStore.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Endpoints;

public static class GenresEndpoints
{
    public static RouteGroupBuilder MapGenresEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("genres").WithTags("Genres");

        group.MapGet("/", async (
            GameStoreContext dbContext,
            CatalogResponseCache catalogCache,
            HttpContext httpContext,
            int page = 1,
            int pageSize = 10,
            string? search = null) =>
        {
            if (page < 1 || pageSize is < 1 or > 100)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid pagination parameters",
                    detail: "Page must be at least 1 and pageSize must be between 1 and 100.");
            }

            var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

            var normalizedSearch = search?.Trim().ToUpperInvariant() ?? string.Empty;
            var cacheKey = $"genres:{page}:{pageSize}:{Uri.EscapeDataString(normalizedSearch)}";
            var response = await catalogCache.GetOrCreateAsync(cacheKey, async () =>
            {
                var genresQuery = dbContext.Genres.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(normalizedSearch))
                {
                    var searchTerm = normalizedSearch.ToLowerInvariant();
                    genresQuery = genresQuery.Where(genre => genre.Name.ToLower().Contains(searchTerm));
                }

                var totalItems = await genresQuery.CountAsync();
                var items = await genresQuery
                    .OrderBy(genre => genre.Id)
                    .Skip(skip)
                    .Take(pageSize)
                    .Select(genre => genre.ToDto())
                    .ToListAsync();

                return new PagedResponseDto<GenreDto>(
                    items,
                    page,
                    pageSize,
                    totalItems,
                    (int)Math.Ceiling(totalItems / (double)pageSize));
            });

            httpContext.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(response);
        })
        .WithName("ListGenres")
        .WithSummary("List genres")
        .WithDescription("Returns a page of genres and supports case-insensitive name search. Page size is limited to 100.")
        .RequireAuthorization();

        return group;
    }
}