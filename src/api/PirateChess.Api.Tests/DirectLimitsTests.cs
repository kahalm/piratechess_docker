using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using PirateChess.Api.Controllers;

namespace PirateChess.Api.Tests;

/// <summary>
/// Rate-Limiter + Request-Größenlimits der /api/chessable/direct-Endpoints.
/// Die Prod-Defaults sind bewusst großzügig (Fortschritts-Polling alle 2,5 s + laufende Importe
/// dürfen nie abreißen) — für den 429-Test wird das Fenster per Config winzig gestellt.
/// </summary>
public class DirectLimitsTests
{
    private const string ServiceKeyHeader = "X-Service-Key";
    private const string ValidServiceKey = "test-service-key";

    /// <summary>Factory mit Mini-Fenster (2 Requests, Fenster läuft während des Tests nicht ab) —
    /// nur so lässt sich das 429-Verhalten deterministisch auslösen.</summary>
    private sealed class TinyRateLimitFactory(int invalidKeyPermitLimit = 30) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimit:Direct:PermitLimit"] = "2",
                    ["RateLimit:Direct:WindowSeconds"] = "3600",
                    ["RateLimit:Direct:InvalidKeyPermitLimit"] = invalidKeyPermitLimit.ToString(),
                });
            });
        }
    }

    /// <summary>Factory ohne konfigurierten Service-Key.</summary>
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
    public async Task Direct_OverPermitLimit_Returns429()
    {
        using var factory = new TinyRateLimitFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ServiceKeyHeader, ValidServiceKey);

        var r1 = await client.GetAsync("/api/chessable/direct/build-info");
        var r2 = await client.GetAsync("/api/chessable/direct/build-info");
        var r3 = await client.GetAsync("/api/chessable/direct/build-info");

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode); // Fenster voll → 429, nicht 503
    }

    [Fact]
    public async Task CallsWithoutValidKey_DoNotEmptyTheServiceKeyWindow()
    {
        // S2-005: vorher EIN Fenster für alle direct-Aufrufer — Aufrufe ohne Key verbrauchten die Permits, rookhubs
        // echte Aufrufe (Poll, course/parse) bekamen 429 und der Import scheiterte.
        using var factory = new TinyRateLimitFactory();
        var anonymous = factory.CreateClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/chessable/direct/build-info")).StatusCode);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ServiceKeyHeader, ValidServiceKey);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/chessable/direct/build-info")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/chessable/direct/build-info")).StatusCode);
    }

    [Fact]
    public async Task CallsWithWrongKey_HaveTheirOwnSmallWindow()
    {
        using var factory = new TinyRateLimitFactory(invalidKeyPermitLimit: 1);
        var wrong = factory.CreateClient();
        wrong.DefaultRequestHeaders.Add(ServiceKeyHeader, "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/chessable/direct/build-info")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await wrong.GetAsync("/api/chessable/direct/build-info")).StatusCode);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ServiceKeyHeader, ValidServiceKey);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/chessable/direct/build-info")).StatusCode);
    }

    [Fact]
    public async Task ParseCourse_WithoutKey_Returns401_BeforeTheBodyIsBound()
    {
        // S2-005: die Key-Prüfung lief als Action-Filter NACH Modellbindung und der ModelState-Prüfung von
        // [ApiController] — ein kaputter Body ohne Key bekam 400 samt Feldnamen, ein großer wurde erst ganz gelesen und
        // deserialisiert (course/parse nimmt bis 100 MB an).
        using var factory = new TestWebApplicationFactory();
        var response = await factory.CreateClient().PostAsync("/api/chessable/direct/course/parse",
            new StringContent("{\"bid\": [", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ServiceKeyNotConfigured_Returns503()
    {
        using var factory = new NoServiceKeyFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ServiceKeyHeader, ValidServiceKey);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/chessable/direct/build-info")).StatusCode);
    }

    [Fact]
    public async Task NonDirectEndpoints_AreNotRateLimited()
    {
        // Die "direct"-Policy hängt NUR an direct/* — /api/health & Co. bleiben unlimitiert
        // (Docker-/LB-Healthchecks dürfen nie in ein 429 laufen).
        using var factory = new TinyRateLimitFactory();
        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var response = await client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // --- Request-Größenlimits: TestServer erzwingt MaxRequestBodySize nicht (kein Kestrel), daher
    // --- werden die Attribute selbst geprüft (Wert über CustomAttributeData-Konstruktorargumente).

    [Fact]
    public void DirectController_HasSmallRequestSizeLimit_AndDirectRateLimitPolicy()
    {
        var attrs = typeof(ChessableDirectController).GetCustomAttributesData();

        // Klassenweit kleines Body-Limit: direct/*-Requests tragen nur Bearer + bid + Mode (wenige KB).
        var size = attrs.Single(a => a.AttributeType == typeof(RequestSizeLimitAttribute));
        Assert.Equal(256L * 1024, (long)size.ConstructorArguments[0].Value!);

        // Fixed-Window-Limiter über die benannte "direct"-Policy.
        var rate = attrs.Single(a => a.AttributeType == typeof(EnableRateLimitingAttribute));
        Assert.Equal("direct", (string)rate.ConstructorArguments[0].Value!);
    }

    [Fact]
    public void ParseCourse_HasLargerRequestSizeLimit_ForBrowserCapturedCourses()
    {
        // course/parse bekommt browser-erfasstes Roh-JSON GANZER Kurse (36+ MB dokumentiert) —
        // das klassenweite Mini-Limit würde den Browser-Import großer Kurse abschneiden.
        var method = typeof(ChessableDirectController).GetMethod(nameof(ChessableDirectController.ParseCourse))!;
        var size = method.GetCustomAttributesData().Single(a => a.AttributeType == typeof(RequestSizeLimitAttribute));
        Assert.Equal(100L * 1024 * 1024, (long)size.ConstructorArguments[0].Value!);
    }
}
