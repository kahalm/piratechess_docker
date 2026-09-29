using System.Net;

namespace PirateChess.Api.Tests;

public class VpnControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public VpnControllerTests(TestWebApplicationFactory factory) => _factory = factory;

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

    // --- POST /rotate: nur der X-Service-Key zählt (ein Nutzer-JWT war selbst ausstellbar → Import-DoS) ---

    [Fact]
    public async Task Rotate_WithoutServiceKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/vpn/rotate", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rotate_WithJwtButWithoutServiceKey_Returns401()
    {
        // Ein Nutzer-JWT allein darf die geteilte Exit-IP nicht rotieren.
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory, "vpn_" + Guid.NewGuid().ToString("N")[..8]);

        var response = await client.PostAsync("/api/vpn/rotate", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rotate_WithServiceKey_Succeeds_WithoutJwt()
    {
        // Der manuelle Trigger hängt nur am Service-Key; mit geschlossener Registrierung gäbe es sonst kein JWT mehr dafür.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Key", "test-service-key");

        var response = await client.PostAsync("/api/vpn/rotate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
