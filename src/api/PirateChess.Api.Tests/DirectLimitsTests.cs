using System.Net;
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
    private sealed class TinyRateLimitFactory : TestWebApplicationFactory
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
                });
            });
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
