using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using PirateChess.Api.Authorization;

namespace PirateChess.Api.Tests;

/// <summary>
/// S3-010 (Begleitteil zum Crawler, W4s-B20): der Dienst-zu-Dienst-Schlüssel verhält sich in piratechess wie im Crawler —
/// Längenangleich per SHA-256 vor dem zeitkonstanten Vergleich, 401/503 als JSON {message}, nur /api/health offen.
/// </summary>
public class ServiceKeyAuthTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ServiceKeyAuthTests(TestWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("test-service-key", "test-service-key", true)]
    [InlineData("schlüssel-ä", "schlüssel-ä", true)]
    [InlineData("test-service-ke", "test-service-key", false)]       // Präfix (kürzer)
    [InlineData("test-service-key-extra", "test-service-key", false)] // länger
    [InlineData("TEST-SERVICE-KEY", "test-service-key", false)]       // Groß/klein zählt
    [InlineData("", "test-service-key", false)]
    public void KeysEqual_ComparesExactly_RegardlessOfLength(string provided, string expected, bool equal)
        => Assert.Equal(equal, ServiceKeyAuthAttribute.KeysEqual(provided, expected));

    [Fact]
    public async Task WrongKey_Returns401_WithJsonMessage()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Key", "nope");

        var response = await client.GetAsync("/api/chessable/direct/build-info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Invalid service key", await MessageOf(response));
    }

    private sealed class NoServiceKeyFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Service:ApiKey"] = "" }));
        }
    }

    [Fact]
    public async Task NoKeyConfigured_Returns503_WithJsonMessage_ButHealthStaysOpen()
    {
        using var factory = new NoServiceKeyFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/chessable/direct/build-info");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Service authentication is not configured", await MessageOf(response));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }

    private static async Task<string?> MessageOf(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var prop = Assert.Single(doc.RootElement.EnumerateObject());
        Assert.Equal("message", prop.Name);
        return prop.Value.GetString();
    }

    /// <summary>
    /// Der Crawler schützt global per Middleware (nur /api/health offen), piratechess per Attribut: ein neuer Endpunkt
    /// ohne [ServiceKeyAuth]/[Authorize] wäre hier still offen. Dieser Wächter hält die Liste der anonymen Aktionen fest —
    /// Health (Liveness) und der Login/die Registrierung der Alt-JWT-Endpunkte (Registrierung standardmäßig aus).
    /// </summary>
    [Fact]
    public void Only_health_and_the_legacy_login_are_reachable_without_service_key_or_jwt()
    {
        static bool Protected(MemberInfo m) =>
            m.GetCustomAttributes<ServiceKeyAuthAttribute>(true).Any() || m.GetCustomAttributes<AuthorizeAttribute>(true).Any();

        var anonymous = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(true).Any())
                .Where(m => !Protected(t) && !Protected(m))
                .Select(m => $"{t.Name}.{m.Name}"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["AuthController.Login", "AuthController.Register", "HealthController.Get"], anonymous);
    }
}
