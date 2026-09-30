using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using piratechess_lib;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>
/// S2-018: ein gecachter Kurs wird je course/start nur EINMAL komplett geladen und entpackt. Ohne Bearer lud
/// das Start-Gate den Kurs, und der Job lud ihn Millisekunden später noch einmal.
/// </summary>
public class CourseStartCacheLoadTests : IClassFixture<TestWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly TestWebApplicationFactory _factory;

    public CourseStartCacheLoadTests(TestWebApplicationFactory factory) => _factory = factory;

    private sealed class CountingRawCourseCache(IServiceScopeFactory scopeFactory, ILogger<RawCourseCache> logger)
        : RawCourseCache(scopeFactory, logger)
    {
        public ConcurrentDictionary<string, int> Loads { get; } = new();

        public override Task<RestResponseCourse?> GetAsync(string bid, CancellationToken ct = default)
        {
            Loads.AddOrUpdate(bid, 1, (_, n) => n + 1);
            return base.GetAsync(bid, ct);
        }
    }

    private WebApplicationFactory<Program> HostWithCountingCache()
        => _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<RawCourseCache>();
            s.AddSingleton<RawCourseCache>(sp => new CountingRawCourseCache(
                sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<RawCourseCache>>()));
        }));

    /// <summary>Vollständiger gecachter Kurs im alten Voll-Blob-Format (Linieninhalt inline) — besteht IsComplete.</summary>
    private static async Task SeedCompleteCachedCourseAsync(IServiceProvider services, string bid)
    {
        var course = new RestResponseCourse
        {
            CourseJsonContent = "{\"course\":{\"data\":[{\"id\":1}]}}",
            ChapterList =
            [
                new RestResponseChapter
                {
                    ChapterJsonContent = "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"}]}}",
                    ResponseLineList =
                    [
                        new RestResponseLine
                        {
                            Oid = 0,
                            LineJsonContent = "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"san\":\"e4\"}]}}"
                        }
                    ]
                }
            ]
        };
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CachedRawCourses.Add(new CachedRawCourse
        {
            Bid = bid,
            RestResponseJson = GzipText.Compress(JsonSerializer.Serialize(course)),
            CachedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task<ProgressResp> RunToEndAsync(HttpClient client, string bid, string bearer)
    {
        var start = await client.PostAsJsonAsync("/api/chessable/direct/course/start", new { Bearer = bearer, Bid = bid, Mode = "None" });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var jobId = (await start.Content.ReadFromJsonAsync<JobStartResp>(JsonOpts))!.JobId;
        for (var i = 0; i < 200; i++)
        {
            var poll = await client.GetFromJsonAsync<ProgressResp>($"/api/chessable/direct/course/{jobId}", JsonOpts);
            if (poll!.Status != "running") return poll;
            await Task.Delay(50);
        }
        throw new TimeoutException("Job nicht fertig geworden");
    }

    [Theory]
    [InlineData("747001", "")]                 // ohne Bearer: Gate lädt, Job nimmt die Daten mit
    [InlineData("747002", "some-valid-jwt")]   // mit Bearer: kein Gate-Laden, der Job lädt einmal
    public async Task CourseStart_CachedCourse_LoadedOnce(string bid, string bearer)
    {
        using var host = HostWithCountingCache();
        await SeedCompleteCachedCourseAsync(host.Services, bid);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Key", "test-service-key");

        var result = await RunToEndAsync(client, bid, bearer);

        Assert.Equal("completed", result.Status);
        Assert.Contains("e4", result.Pgn);
        var cache = (CountingRawCourseCache)host.Services.GetRequiredService<RawCourseCache>();
        Assert.Equal(1, cache.Loads.GetValueOrDefault(bid));
    }

    private record JobStartResp(string JobId);
    private record ProgressResp(string Status, string? Pgn, string? Error);
}
