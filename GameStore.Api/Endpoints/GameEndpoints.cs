namespace GameStore.Api.Endpoints;

using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using GameStore.Api.Mapping;
using Microsoft.EntityFrameworkCore;

public static class GameEndpoints
{
    const string GetGameEndpointName = "GetGame";

    public static RouteGroupBuilder MapGamesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("games").WithParameterValidation().WithTags("Games");

        group.MapGet("/", async (
            GameStoreContext dbContext,
            ILoggerFactory loggerFactory,
            int page = 1,
            int pageSize = 10,
            int? genreId = null,
            decimal? minPrice = null,
            decimal? maxPrice = null,
            string? search = null) =>
        {
            var logger = loggerFactory.CreateLogger("GameStore.Api.Endpoints.Games");

            if (page < 1 || pageSize is < 1 or > 100)
            {
                logger.LogWarning("Game list request rejected because pagination values are invalid");
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid pagination parameters",
                    detail: "Page must be at least 1 and pageSize must be between 1 and 100.");
            }

            var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

            if (minPrice < 0 || maxPrice < 0 ||
                (minPrice.HasValue && maxPrice.HasValue && minPrice > maxPrice))
            {
                logger.LogWarning("Game list request rejected because the price range is invalid");
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid price range",
                    detail: "Prices cannot be negative, and minPrice cannot exceed maxPrice.");
            }

            var gamesQuery = dbContext.Games
                .Include(game => game.Genre)
                .AsNoTracking()
                .AsQueryable();

            if (genreId.HasValue)
            {
                gamesQuery = gamesQuery.Where(game => game.GenreId == genreId.Value);
            }

            if (minPrice.HasValue)
            {
                gamesQuery = gamesQuery.Where(game => game.Price >= minPrice.Value);
            }

            if (maxPrice.HasValue)
            {
                gamesQuery = gamesQuery.Where(game => game.Price <= maxPrice.Value);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var searchTerm = search.Trim().ToLower();
                gamesQuery = gamesQuery.Where(game =>
                    game.Name.ToLower().Contains(searchTerm) ||
                    game.Genre!.Name.ToLower().Contains(searchTerm));
            }

            var totalItems = await gamesQuery.CountAsync();
            var items = await gamesQuery
                .OrderBy(game => game.Id)
                .Skip(skip)
                .Take(pageSize)
                .Select(game => game.ToGameSummaryDto())
                .ToListAsync();

            return Results.Ok(new PagedResponseDto<GameSummaryDto>(
                items,
                page,
                pageSize,
                totalItems,
                (int)Math.Ceiling(totalItems / (double)pageSize)));
        })
        .WithName("ListGames")
        .WithSummary("List games with filters")
        .WithDescription("Returns a page of games. Supports case-insensitive search by game or genre name, genre ID, and minimum/maximum price filters. Page size is limited to 100.")
        .RequireAuthorization();

        group.MapGet("/{id}", async (int id, GameStoreContext dbContext, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("GameStore.Api.Endpoints.Games");
            Game? game = await dbContext.Games.FindAsync(id);

            return game is null
                ? LogGameNotFound(logger, id)
                : Results.Ok(game.ToGameDetailsDto());
        }
            )
            .WithName(GetGameEndpointName)
            .WithSummary("Get a game")
            .WithDescription("Returns game details, or ProblemDetails with 404 when the ID does not exist.")
            .RequireAuthorization();

        group.MapPost("/", async (CreateGameDto newGame, GameStoreContext dbContext, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("GameStore.Api.Endpoints.Games");

            if (!await dbContext.Genres.AnyAsync(genre => genre.Id == newGame.GenreId))
            {
                logger.LogWarning("Game creation rejected because genre ID {GenreId} does not exist", newGame.GenreId);
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid genre",
                    detail: $"No genre exists with ID {newGame.GenreId}.");
            }

            Game game = newGame.ToEntity();

            dbContext.Games.Add(game);
            await dbContext.SaveChangesAsync();

            logger.LogInformation("Created game with ID {GameId} in genre ID {GenreId}", game.Id, game.GenreId);
            return Results.CreatedAtRoute(GetGameEndpointName, new { id = game.Id }, game.ToGameDetailsDto());
        })
        .WithSummary("Create a game")
        .WithDescription("Creates a game when the supplied genre exists. Requires the admin role.")
        .RequireAuthorization("CanManageGames");

        group.MapPut("/{id}", async (int id, UpdateGameDto updatedGame, GameStoreContext dbContext, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("GameStore.Api.Endpoints.Games");
            var existingGame = await dbContext.Games.FindAsync(id);

            if (existingGame is null)
            {
                return LogGameNotFound(logger, id);
            }

            if (!await dbContext.Genres.AnyAsync(genre => genre.Id == updatedGame.GenreId))
            {
                logger.LogWarning("Game update rejected for game ID {GameId}: genre ID {GenreId} does not exist", id, updatedGame.GenreId);
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid genre",
                    detail: $"No genre exists with ID {updatedGame.GenreId}.");
            }

            dbContext.Entry(existingGame).CurrentValues.SetValues(updatedGame.ToEntity(id));
            await dbContext.SaveChangesAsync();

            logger.LogInformation("Updated game with ID {GameId}", id);
            return Results.NoContent();
        })
        .WithSummary("Update a game")
        .WithDescription("Updates a game and validates that its genre exists. Requires the admin role.")
        .RequireAuthorization("CanManageGames");

        group.MapDelete("/{id}", async (int id, GameStoreContext dbContext, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("GameStore.Api.Endpoints.Games");
            var deletedCount = await dbContext.Games
                .Where(game => game.Id == id)
                .ExecuteDeleteAsync();

            if (deletedCount == 0)
            {
                return LogGameNotFound(logger, id);
            }

            logger.LogInformation("Deleted game with ID {GameId}", id);
            return Results.NoContent();
        })
        .WithSummary("Delete a game")
        .WithDescription("Deletes a game by ID. Returns 404 if the game does not exist. Requires the admin role.")
        .RequireAuthorization("CanManageGames");

        return group;
    }

    private static IResult LogGameNotFound(ILogger logger, int id)
    {
        logger.LogWarning("Game with ID {GameId} was not found", id);
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Game not found",
            detail: $"No game exists with ID {id}.");
    }
}