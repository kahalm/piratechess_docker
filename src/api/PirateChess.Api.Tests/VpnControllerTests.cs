using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PirateChess.Api.Tests;

public class VpnControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public VpnControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    /// <summary>Registriert einen frischen Nutzer über die OFFENE Registrierung und liefert sein JWT —
    /// genau der Weg, über den sich ein Fremder ein Token besorgen könnte.</summary>
    private static async Task<string> RegisterAndGetJwtAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = "vpn_" + Guid.NewGuid().ToString("N")[..8],
            Email = $"vpn_{Guid.NewGuid():N}@test.com",
            Password = "Test1234!"
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task Status_WithoutServiceKey_Returns401()
    {
        // /api/vpn/status gibt die reale Exit-IP preis → ohne X-Service-Key abgelehnt.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/vpn/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Status_WithWrongServiceKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Key", "nope");
        var response = await client.GetAsync("/api/vpn/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- POST /rotate: JWT allein reicht NICHT mehr (offene Registrierung → Import-DoS) ---

    [Fact]
    public async Task Rotate_WithJwtButWithoutServiceKey_Returns401()
    {
        // Registrierung ist offen → jeder Selbst-Registrierte hätte ein JWT. Rotieren der geteilten
        // Exit-IP darf damit allein NICHT möglich sein: /rotate verlangt zusätzlich den X-Service-Key.
        var client = _factory.CreateClient();
        var jwt = await RegisterAndGetJwtAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await client.PostAsync("/api/vpn/rotate", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rotate_WithJwtAndServiceKey_Succeeds()
    {
        // Gegenprobe: mit BEIDEN Schranken (JWT + Service-Key) funktioniert der manuelle Trigger weiter.
        var client = _factory.CreateClient();
        var jwt = await RegisterAndGetJwtAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        client.DefaultRequestHeaders.Add("X-Service-Key", "test-service-key");

        var response = await client.PostAsync("/api/vpn/rotate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rotate_WithServiceKeyButWithoutJwt_Returns401()
    {
        // Auch der Service-Key allein reicht nicht — [Authorize] bleibt bestehen (strikt enger als vorher).
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Key", "test-service-key");

        var response = await client.PostAsync("/api/vpn/rotate", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
