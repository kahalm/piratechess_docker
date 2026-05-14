using System.Net;
using System.Net.Http.Json;

namespace PirateChess.Api.Tests;

public class HealthControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public HealthControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetHealth_ReturnsHealthy()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Got {response.StatusCode}: {content}");
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("healthy", body!.Status);
        Assert.True(body.Database);
    }

    private record HealthResponse(string Status, bool Database);
}
