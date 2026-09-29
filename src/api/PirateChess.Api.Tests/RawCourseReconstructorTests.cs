using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class RawCourseReconstructorTests
{
    private static (RawCourseReconstructor rec, RawCourseCache cache, IServiceScopeFactory sf) Build(
        int maxUnusableLines = RawCourseCache.DefaultMaxUnusableLines)
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var sf = sp.GetRequiredService<IServiceScopeFactory>();
        var cache = new RawCourseCache(sf, NullLogger<RawCourseCache>.Instance, maxUnusableLines: maxUnusableLines);
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
    public async Task Reconstruct_ToleratesTruncatedLine_StillBuildsCache_AndReports()
    {
        var (rec, cache, sf) = Build();

        // getCourse (1 Kapitel), getList mit 3 Linien; oid=100/101 sauber, oid=102 abgeschnitten
        // (nicht leer, aber unparsbar). Frisches Nachladen ist bei unowned Kursen unmöglich → als Lücke
        // tolerieren (usable 2 > dead 1, innerhalb Toleranz) statt die Rekonstruktion zu kippen.
        using (var scope = sf.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChessableRawResponses.Add(new ChessableRawResponse
            {
                Endpoint = "course",
                Url = "https://www.chessable.com/api/v1/getCourse?uid=1&bid=778&includeVariations=true",
                RawJson = GzipText.Compress("{\"course\":{\"data\":[{\"id\":1,\"total\":3}]}}"),
                RequestedAt = DateTime.UtcNow
            });
            db.ChessableRawResponses.Add(new ChessableRawResponse
            {
                Endpoint = "chapter",
                Url = "https://www.chessable.com/api/v1/getList?uid=1&bid=778&lid=1",
                RawJson = GzipText.Compress("{\"list\":{\"data\":[{\"id\":100},{\"id\":101},{\"id\":102}]}}"),
                RequestedAt = DateTime.UtcNow
            });
            db.CachedRawLines.Add(new CachedRawLine { Oid = 100, LineJsonContent = GzipText.Compress("{\"game\":{}}"), CachedAt = DateTime.UtcNow });
            db.CachedRawLines.Add(new CachedRawLine { Oid = 101, LineJsonContent = GzipText.Compress("{\"game\":{}}"), CachedAt = DateTime.UtcNow });
            // Abgeschnitten: nicht leer, aber kein gültiges JSON → unparsbar.
            db.CachedRawLines.Add(new CachedRawLine { Oid = 102, LineJsonContent = GzipText.Compress("{\"game\":{"), CachedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var r = await rec.ReconstructAsync("778");

        Assert.True(r.Ok, r.Error);
        Assert.Equal(3, r.Lines);
        Assert.Equal(0, r.MissingLines);
        Assert.Equal(1, r.UnparseableLines);
        var got = await cache.GetAsync("778");
        Assert.NotNull(got);
        Assert.True(string.IsNullOrEmpty(got!.ChapterList[0].ResponseLineList.Single(l => l.Oid == 102).LineJsonContent));

        // Markiert statt gelöscht: CachedRawLines ist der einzige dauerhafte Linien-Speicher.
        using var verify = sf.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(3, await vdb.CachedRawLines.CountAsync());
        var row = await vdb.CachedRawLines.SingleAsync(c => c.Oid == 102);
        Assert.NotNull(row.InvalidAt);
        Assert.StartsWith("JSON:", row.InvalidReason);
        Assert.Equal("{\"game\":{", GzipText.Decompress(row.LineJsonContent));
    }

    private static async Task SeedCourseAsync(IServiceScopeFactory sf, string bid, params int[] oids)
    {
        using var scope = sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChessableRawResponses.Add(new ChessableRawResponse
        {
            Endpoint = "course",
            Url = $"https://www.chessable.com/api/v1/getCourse?uid=1&bid={bid}&includeVariations=true",
            RawJson = GzipText.Compress("{\"course\":{\"data\":[{\"id\":1}]}}"),
            RequestedAt = DateTime.UtcNow
        });
        db.ChessableRawResponses.Add(new ChessableRawResponse
        {
            Endpoint = "chapter",
            Url = $"https://www.chessable.com/api/v1/getList?uid=1&bid={bid}&lid=1",
            RawJson = GzipText.Compress("{\"list\":{\"data\":[" + string.Join(",", oids.Select(o => $"{{\"id\":{o}}}")) + "]}}"),
            RequestedAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddLineAsync(IServiceScopeFactory sf, int oid, string content, DateTime? invalidAt = null, string? reason = null)
    {
        using var scope = sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CachedRawLines.Add(new CachedRawLine
        {
            Oid = oid, LineJsonContent = GzipText.Compress(content), CachedAt = DateTime.UtcNow,
            InvalidAt = invalidAt, InvalidReason = reason,
        });
        await db.SaveChangesAsync();
    }

    // Scheitert die Rekonstruktion („Cache NICHT geschrieben"), darf sie vorher nichts angefasst haben —
    // früher waren die unbrauchbaren Zeilen da schon per RemoveRange weg.
    [Fact]
    public async Task Reconstruct_TooManyUnusableLines_Fails_WithoutTouchingTheLineCache()
    {
        var (rec, cache, sf) = Build();
        var oids = Enumerable.Range(300, 8).ToArray();
        await SeedCourseAsync(sf, "780", oids);
        await AddLineAsync(sf, 300, "{\"game\":{}}");
        await AddLineAsync(sf, 301, "{\"game\":{}}");
        foreach (var oid in oids.Skip(2))
            await AddLineAsync(sf, oid, "{\"game\":{");          // 6 abgeschnittene > Toleranz 5

        var r = await rec.ReconstructAsync("780");

        Assert.False(r.Ok);
        Assert.Equal(6, r.UnparseableLines);
        Assert.Null(await cache.GetAsync("780"));
        using var scope = sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(8, await db.CachedRawLines.CountAsync());
        Assert.False(await db.CachedRawLines.AnyAsync(c => c.InvalidAt != null));   // keine Schreibaktion
    }

    // Eine schon markierte Linie, die der heutige Parser nicht liest (softFail als Objekt, bid 2033 — aufgehoben
    // für einen späteren Parser-Fix), wurde bei der Rekonstruktion unwiderruflich gelöscht.
    [Fact]
    public async Task Reconstruct_AlreadyMarkedLine_KeepsItsMarkAndContent()
    {
        var (rec, cache, sf) = Build();
        const string softFailObject = "{\"game\":{\"softFail\":{\"1\":{\"w\":[\"e4\"]}}}}";
        var markedAt = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        await SeedCourseAsync(sf, "781", 400, 401, 402);
        await AddLineAsync(sf, 400, "{\"game\":{}}");
        await AddLineAsync(sf, 401, "{\"game\":{}}");
        await AddLineAsync(sf, 402, softFailObject, markedAt, "JSON: softFail");

        var r = await rec.ReconstructAsync("781");

        Assert.True(r.Ok, r.Error);
        Assert.Equal(1, r.UnparseableLines);
        Assert.NotNull(await cache.GetAsync("781"));
        using var scope = sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.CachedRawLines.SingleAsync(c => c.Oid == 402);
        Assert.Equal(markedAt, row.InvalidAt);                       // ursprüngliche Markierung bleibt
        Assert.Equal("JSON: softFail", row.InvalidReason);
        Assert.Equal(softFailObject, GzipText.Decompress(row.LineJsonContent));
    }

    // Die neueste getList-Antwort im Audit ist ein Fehlerkörper (Token mitten im Import abgelaufen) → die
    // Rekonstruktion nimmt die ältere, gültige Antwort statt eines Kapitels ohne Linien.
    [Fact]
    public async Task Reconstruct_SkipsAChapterErrorBody_UsesTheOlderValidResponse()
    {
        var (rec, cache, sf) = Build();
        await SeedCourseAsync(sf, "782", 500, 501);
        await AddLineAsync(sf, 500, "{\"game\":{}}");
        await AddLineAsync(sf, 501, "{\"game\":{}}");
        using (var scope = sf.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChessableRawResponses.Add(new ChessableRawResponse
            {
                Endpoint = "chapter",
                Url = "https://www.chessable.com/api/v1/getList?uid=1&bid=782&lid=1",
                RawJson = GzipText.Compress("{\"error\":{\"message\":\"Expired token\"}}"),
                RequestedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var r = await rec.ReconstructAsync("782");

        Assert.True(r.Ok, r.Error);
        Assert.Equal(2, r.Lines);
        Assert.NotNull(await cache.GetAsync("782"));
    }

    // Der Pre-Write-Gate muss mit der INSTANZ-Toleranz des Caches prüfen, nicht mit dem statischen
    // Default: sonst meldet ReconstructAsync bei strenger konfiguriertem Cache Ok=true, während
    // SetAsync das Schreiben still verweigert (Aufrufer glaubt an einen Cache, den es nie gibt).
    [Fact]
    public async Task Reconstruct_UsesCacheInstanceTolerance_NotStaticDefault()
    {
        var (rec, cache, sf) = Build(maxUnusableLines: 0);   // strenger als der Default (5)

        // 3 Linien: 2 sauber, 1 fehlt → 1 Lücke, mit Default-Toleranz ok, mit 0 nicht.
        using (var scope = sf.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChessableRawResponses.Add(new ChessableRawResponse
            {
                Endpoint = "course",
                Url = "https://www.chessable.com/api/v1/getCourse?uid=1&bid=779&includeVariations=true",
                RawJson = GzipText.Compress("{\"course\":{\"data\":[{\"id\":1,\"total\":3}]}}"),
                RequestedAt = DateTime.UtcNow
            });
            db.ChessableRawResponses.Add(new ChessableRawResponse
            {
                Endpoint = "chapter",
                Url = "https://www.chessable.com/api/v1/getList?uid=1&bid=779&lid=1",
                RawJson = GzipText.Compress("{\"list\":{\"data\":[{\"id\":200},{\"id\":201},{\"id\":202}]}}"),
                RequestedAt = DateTime.UtcNow
            });
            db.CachedRawLines.Add(new CachedRawLine { Oid = 200, LineJsonContent = GzipText.Compress("{\"game\":{}}"), CachedAt = DateTime.UtcNow });
            db.CachedRawLines.Add(new CachedRawLine { Oid = 201, LineJsonContent = GzipText.Compress("{\"game\":{}}"), CachedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var r = await rec.ReconstructAsync("779");

        Assert.False(r.Ok);                          // vorher: Ok=true, aber SetAsync schrieb nie
        Assert.Null(await cache.GetAsync("779"));
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
