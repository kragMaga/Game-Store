using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GameStore.Api.Tests;

public sealed class ApiErrorResponseTests
{
    [Fact]
    public async Task Swagger_document_exposes_api_routes_and_bearer_auth_for_protected_operations()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        using var document = await client.GetFromJsonAsync<JsonDocument>("/swagger/v1/swagger.json");

        Assert.NotNull(document);
        var root = document.RootElement;
        Assert.Equal("Game Store API", root.GetProperty("info").GetProperty("title").GetString());
        var paths = root.GetProperty("paths");
        Assert.Contains(paths.EnumerateObject(), path => path.Name.StartsWith("/games", StringComparison.Ordinal));

        var loginOperation = paths.GetProperty("/auth/login").GetProperty("post");
        Assert.False(loginOperation.TryGetProperty("security", out _));

        var currentUserOperation = paths.GetProperty("/auth/me").GetProperty("get");
        Assert.True(currentUserOperation.TryGetProperty("security", out var security));
        Assert.Equal("Bearer", security[0].EnumerateObject().First().Name);
    }

    [Fact]
    public async Task Register_with_valid_details_returns_success()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            username = "player-one",
            password = "Password123"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Register_with_existing_username_returns_conflict_problem()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            username = "Admin",
            password = "Password123"
        });

        using var problem = await AssertProblemAsync(response, HttpStatusCode.Conflict, "Username is already taken");
    }

    [Theory]
    [InlineData("ab", "Password123", "Invalid username")]
    [InlineData("player1", "short", "Password is too short")]
    public async Task Register_with_invalid_fields_returns_actionable_problem(
        string username,
        string password,
        string expectedTitle)
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/register", new { username, password });

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedTitle);
    }

    [Fact]
    public async Task Login_with_invalid_credentials_returns_generic_unauthorized_problem()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "missing-user",
            password = "WrongPassword123"
        });

        using var problem = await AssertProblemAsync(
            response,
            HttpStatusCode.Unauthorized,
            "Authentication failed");
        Assert.Equal("The username or password is incorrect.", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Protected_endpoint_without_token_returns_problem_details()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/auth/me");

        using var problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Game_lookup_for_missing_id_returns_not_found_problem()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        await SignInAsAdminAsync(client);

        var response = await client.GetAsync("/games/987654");

        using var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound, "Game not found");
        Assert.Equal("No game exists with ID 987654.", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Create_game_with_unknown_genre_returns_bad_request_problem()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        await SignInAsAdminAsync(client);

        var response = await client.PostAsJsonAsync("/games", new
        {
            name = "Test Game",
            genreId = 987654,
            price = 29.99m,
            releasedDate = "2026-01-01"
        });

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Invalid genre");
        Assert.Equal("No genre exists with ID 987654.", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Games_can_be_searched_filtered_and_paginated()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        await SignInAsAdminAsync(client);
        await CreateGameAsync(client, "Zelda: Dawn", 1, 20);
        await CreateGameAsync(client, "Zelda: Dusk", 1, 40);
        await CreateGameAsync(client, "Racing Star", 4, 30);

        using var firstPage = await client.GetFromJsonAsync<JsonDocument>(
            "/games?page=1&pageSize=1&genreId=1&minPrice=20&maxPrice=40&search=ZELDA");
        using var secondPage = await client.GetFromJsonAsync<JsonDocument>(
            "/games?page=2&pageSize=1&genreId=1&minPrice=20&maxPrice=40&search=ZELDA");

        Assert.NotNull(firstPage);
        Assert.NotNull(secondPage);
        Assert.Equal(2, firstPage.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, firstPage.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal(1, firstPage.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, secondPage.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, firstPage.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(2, secondPage.RootElement.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Genre_list_is_searchable_and_paginated()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        await SignInAsAdminAsync(client);

        using var response = await client.GetFromJsonAsync<JsonDocument>("/Genres?page=1&pageSize=2&search=SPORT");

        Assert.NotNull(response);
        Assert.Equal(1, response.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal("Sports", response.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("/games?page=0", "Invalid pagination parameters")]
    [InlineData("/games?pageSize=101", "Invalid pagination parameters")]
    [InlineData("/games?minPrice=50&maxPrice=10", "Invalid price range")]
    [InlineData("/Genres?page=0", "Invalid pagination parameters")]
    public async Task Collection_query_validation_returns_problem_details(string url, string expectedTitle)
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        await SignInAsAdminAsync(client);

        var response = await client.GetAsync(url);

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedTitle);
    }

    private static async Task SignInAsAdminAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "Admin",
            password = "Admin123"
        });
        response.EnsureSuccessStatusCode();

        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var token = body.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task CreateGameAsync(HttpClient client, string name, int genreId, decimal price)
    {
        var response = await client.PostAsJsonAsync("/games", new
        {
            name,
            genreId,
            price,
            releasedDate = "2026-01-01"
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonDocument> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedTitle)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal((int)expectedStatus, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(expectedTitle, body.RootElement.GetProperty("title").GetString());
        return body;
    }
}

// AUTOMATEDTEST SCRIPT
// dotnet test GameStore.Api.Tests/GameStore.Api.Tests.csproj
