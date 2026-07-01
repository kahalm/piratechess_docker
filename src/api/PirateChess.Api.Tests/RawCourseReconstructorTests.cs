using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class RawCourseReconstructorTests
{
    private static (RawCourseReconstructor rec, RawCourseCache cache, IServiceScopeFactory sf) Build()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var sf = sp.GetRequiredService<IServiceScopeFactory>();
        var cache = new RawCourseCache(sf, NullLogger<RawCourseCache>.Instance);
        var rec = new RawCourseReconstructor(sf, cache, NullLogger<RawCourseReconstructor>.Instance);
        return (rec, cache, sf);
    }

    [Theory]
    [InlineData("https://x/getCourse?uid=1&bid=5193&includeVariations=true", "bid", "5193", true)]
    [InlineData("https://x/getCourse?uid=1&bid=51930", "bid", "5193", false)]   // kein Präfix-Match
    [InlineData("https://x/getList?uid=1&bid=5193&lid=7", "lid", "7", true)]
    [InlineData("https://x/getGame?uid=1&oid=100", "oid", "100", true)]
    public void UrlHasParam_ExactMatch(string url, string key, string val, bool expected)
        => Assert.Equal(expected, RawCourseReconstructor.UrlHasParam(url, key, val));

    [Fact]
    public async Task Reconstruct_FromStoredRawData_BuildsCache()
    {
        var (rec, cache, sf) = Build();

        // Rohantworten seeden: getCourse (1 Kapitel lid=1), getList (1 Linie oid=100) + Linien-Cache.
        using (var scope = sf.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChessableRawResponses.Add(new ChessableRawResponse
            {
                Endpoint = "course",
                Url = "https://www.chessable.com/api/v1/getCourse?uid=1&bid=777&includeVariations=true",
                RawJson = GzipText.Compress("{\"course\":{\"data\":[{\"id\":1,\"total\":1}]}}"),
                RequestedAt = DateTime.UtcNow
            });
            db.ChessableRawResponses.Add(new ChessableRawResponse
            {
                Endpoint = "chapter",
                Url = "https://www.chessable.com/api/v1/getList?uid=1&bid=777&lid=1",
                RawJson = GzipText.Compress("{\"list\":{\"data\":[{\"id\":100}]}}"),
                RequestedAt = DateTime.UtcNow
            });
            db.CachedRawLines.Add(new CachedRawLine
            {
                Oid = 100,
                LineJsonContent = GzipText.Compress("{\"game\":{}}"),
                CachedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var r = await rec.ReconstructAsync("777");

        Assert.True(r.Ok, r.Error);
        Assert.Equal(1, r.Chapters);
        Assert.Equal(1, r.Lines);
        Assert.Equal(0, r.MissingLines);

        // Der servable Cache ist jetzt gefüllt → Import kann ohne Chessable bedient werden.
        Assert.NotNull(await cache.GetAsync("777"));
    }

    [Fact]
    public async Task Reconstruct_NoStoredCourse_Fails()
    {
        var (rec, _, _) = Build();
        var r = await rec.ReconstructAsync("999");
        Assert.False(r.Ok);
        Assert.Contains("getCourse", r.Error!);
    }
}
