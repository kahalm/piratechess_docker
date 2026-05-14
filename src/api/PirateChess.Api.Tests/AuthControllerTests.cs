using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PirateChess.Api.Tests;

public class AuthControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public AuthControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_ReturnsTokenAndUsername()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = "newuser_" + Guid.NewGuid().ToString("N")[..8],
            Email = $"new_{Guid.NewGuid():N}@test.com",
            Password = "Test1234!"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResp>(JsonOpts);
        Assert.False(string.IsNullOrEmpty(body!.Token));
        Assert.False(string.IsNullOrEmpty(body.Username));
    }

    [Fact]
    public async Task Register_DuplicateUsername_ReturnsConflict()
    {
        var client = _factory.CreateClient();
        var username = "dup_" + Guid.NewGuid().ToString("N")[..8];

        await client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = username,
            Email = $"{username}@test.com",
            Password = "Test1234!"
        });

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = username,
            Email = $"{username}2@test.com",
            Password = "Test1234!"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsToken()
    {
        var client = _factory.CreateClient();
        var username = "login_" + Guid.NewGuid().ToString("N")[..8];

        await client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = username,
            Email = $"{username}@test.com",
            Password = "Test1234!"
        });

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Username = username,
            Password = "Test1234!"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResp>(JsonOpts);
        Assert.False(string.IsNullOrEmpty(body!.Token));
    }

    [Fact]
    public async Task Login_InvalidCredentials_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Username = "nonexistent",
            Password = "wrong"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private record AuthResp(string Token, string Username);
}
