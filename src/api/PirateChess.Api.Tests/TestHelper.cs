using Microsoft.Extensions.DependencyInjection;
using PirateChess.Api.Models.DTOs;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public static class TestHelper
{
    public static async Task<(HttpClient Client, string Token)> CreateAuthenticatedClientAsync(
        TestWebApplicationFactory factory, string username = "testuser", string password = "Test1234!")
    {
        var client = factory.CreateClient();

        // Die HTTP-Registrierung ist standardmäßig aus (Auth:RegistrationEnabled) → Nutzer direkt über den
        // AuthService anlegen bzw., wenn es ihn schon gibt, einloggen.
        using var scope = factory.Services.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<AuthService>();
        var auth = await authService.RegisterAsync(new RegisterRequest(username, $"{username}@test.com", password))
            ?? await authService.LoginAsync(new LoginRequest(username, password))
            ?? throw new InvalidOperationException($"Test user {username} could not be created or logged in");

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.Token);

        return (client, auth.Token);
    }
}
