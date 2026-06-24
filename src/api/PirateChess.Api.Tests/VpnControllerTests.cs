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
}
