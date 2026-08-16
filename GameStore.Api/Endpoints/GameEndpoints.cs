namespace GameStore.Api.Endpoints;

using GameStore.Api.Dtos;

public static class GameEndpoints
{
    const string GetGameEndpointName = "GetGame";

    private static readonly List<GameDto> games = [
new(
        1,
        "The Witcher 3",
        "RPG",
        39.99m,
        new DateOnly(2015, 5, 19)
    ),
    new(
        2,
        "Minecraft",
        "Sandbox",
        29.99m,
        new DateOnly(2011, 11, 18)
    ),
    new(
        3,
        "Forza Horizon 5",
        "Racing",
        59.99m,
        new DateOnly(2021, 11, 9)
    ),
    new(
        4,
        "Halo Infinite",
        "Shooter",
        49.99m,
        new DateOnly(2021, 12, 8)
    ),
    new(
        5,
        "Hades",
        "Roguelike",
        24.99m,
        new DateOnly(2020, 9, 17)
    )];

    public static RouteGroupBuilder MapGamesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("games").WithParameterValidation();
        group.MapGet("/", () => games);

        group.MapGet("/{id}", (int id) =>
        {
            var game = games.Find(game => game.Id == id);

            return game is null ? Results.NotFound() : Results.Ok(game);
        }
            ).WithName(GetGameEndpointName);

        group.MapPost("/", (CreateGameDto newGame) =>
        {
            GameDto game = new(
                games.Count + 1,
                newGame.Name,
                newGame.Genre,
                newGame.Price,
                newGame.ReleasedDate
            );

            games.Add(game);

            return Results.CreatedAtRoute(GetGameEndpointName, new { id = game.Id }, game);
        });

        group.MapPut("/{id}", (int id, UpdateGameDto updatedGame) =>
        {
            var index = games.FindIndex(game => game.Id == id);

            if (index == -1)
            {
                return Results.NotFound();
            }

            games[index] = new GameDto(
            id,
            updatedGame.Name,
            updatedGame.Genre,
            updatedGame.Price,
            updatedGame.ReleasedDate
            );

            return Results.NoContent();
        });


        group.MapDelete("/{id}", (int id) =>
        {
            games.RemoveAll(game => game.Id == id);

            return Results.NoContent();
        });

        return group;
    }
}