using System.Net.Http.Json;
using System.Text.Json;

namespace PirateChess.Api.Tests;

public static class TestHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public record AuthResponse(string Token, string Username);

    public static async Task<(HttpClient Client, string Token)> CreateAuthenticatedClientAsync(
        TestWebApplicationFactory factory, string username = "testuser", string password = "Test1234!")
    {
        var client = factory.CreateClient();

        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = username,
            Email = $"{username}@test.com",
            Password = password
        });

        AuthResponse? auth;
        if (registerResponse.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new
            {
                Username = username,
                Password = password
            });
            loginResponse.EnsureSuccessStatusCode();
            auth = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        }
        else
        {
            registerResponse.EnsureSuccessStatusCode();
            auth = await registerResponse.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        }

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth!.Token);

        return (client, auth.Token);
    }
}
