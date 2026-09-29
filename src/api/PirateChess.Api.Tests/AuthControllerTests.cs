using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PirateChess.Api.Data;

namespace PirateChess.Api.Tests;

public class AuthControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _defaultFactory;
    // Die Registrierung ist standardmäßig aus; die Tests des Registrier-/Login-Pfads schalten sie per Konfiguration ein.
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public AuthControllerTests(TestWebApplicationFactory factory)
    {
        _defaultFactory = factory;
        _factory = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:RegistrationEnabled"] = "true" })));
    }

    [Fact]
    public async Task Register_IsDisabledByDefault_Returns404_AndCreatesNoUser()
    {
        // Ohne Schalter stellt die offene Registrierung niemandem mehr ein JWT aus (kein Aufrufer, S2-004).
        var client = _defaultFactory.CreateClient();
        var username = "closed_" + Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = username,
            Email = $"{username}@test.com",
            Password = "Test1234!"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scope = _defaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(db.Users.Any(u => u.Username == username));
    }

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
