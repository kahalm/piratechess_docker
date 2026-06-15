using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using piratechess_lib;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class RawCourseCacheTests
{
    private static RawCourseCache BuildCache()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString(); // einmal festlegen → alle Scopes teilen denselben Store
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        return new RawCourseCache(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RawCourseCache>.Instance);
    }

    // Minimal vollständiger Kurs (1 Kapitel, 1 Linie mit Content) — sonst lehnt SetAsync das Cachen ab.
    private static RestResponseCourse Complete(string courseJson)
    {
        var c = new RestResponseCourse { CourseJsonContent = courseJson };
        var ch = new RestResponseChapter { ChapterJsonContent = "{\"list\":{}}" };
        ch.ResponseLineList.Add(new RestResponseLine { LineJsonContent = "{\"game\":{}}" });
        c.ChapterList.Add(ch);
        return c;
    }

    [Fact]
    public async Task SetThenGet_ByBid_RoundTrips()
    {
        var cache = BuildCache();

        await cache.SetAsync("bid1", Complete("{\"course\":{\"data\":[]}}"));

        var got = await cache.GetAsync("bid1");
        Assert.NotNull(got);
        Assert.Equal("{\"course\":{\"data\":[]}}", got!.CourseJsonContent);
        Assert.Null(await cache.GetAsync("other-bid")); // anderer Kurs → kein Treffer
    }

    [Fact]
    public async Task Set_Twice_UpdatesSameBid()
    {
        var cache = BuildCache();
        await cache.SetAsync("bid1", Complete("v1"));
        await cache.SetAsync("bid1", Complete("v2"));

        var got = await cache.GetAsync("bid1");
        Assert.Equal("v2", got!.CourseJsonContent);
    }

    // --- Cache-Härtung: unvollständige Kurse NICHT cachen (Regression bid 116242) ---

    [Fact]
    public void IsComplete_FullCourse_True()
    {
        Assert.True(RawCourseCache.IsComplete(Complete("{}")));
    }

    [Fact]
    public void IsComplete_NoChapters_False()
    {
        Assert.False(RawCourseCache.IsComplete(new RestResponseCourse { CourseJsonContent = "{}" }));
        Assert.False(RawCourseCache.IsComplete(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void IsComplete_EmptyLineContent_False(string lineContent)
    {
        var c = Complete("{}");
        c.ChapterList[0].ResponseLineList.Add(new RestResponseLine { LineJsonContent = lineContent });
        Assert.False(RawCourseCache.IsComplete(c));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    public void IsComplete_EmptyChapterContent_False(string chapterContent)
    {
        var c = Complete("{}");
        c.ChapterList[0].ChapterJsonContent = chapterContent;
        Assert.False(RawCourseCache.IsComplete(c));
    }

    [Fact]
    public async Task Set_IncompleteCourse_NotCached()
    {
        var cache = BuildCache();
        var poisoned = Complete("{\"course\":{}}");
        poisoned.ChapterList[0].ResponseLineList.Add(new RestResponseLine { LineJsonContent = "" }); // leere Linie

        await cache.SetAsync("bidX", poisoned);

        Assert.Null(await cache.GetAsync("bidX")); // wurde NICHT gecacht
    }

    // --- Truncation-Härtung (prod): abgeschnittenes (nicht-leeres) Kapitel-JSON ---

    [Fact]
    public void IsComplete_TruncatedChapterContent_False()
    {
        var c = Complete("{}");
        // Gültiger Anfang, mitten im data-Array abgeschnitten (≈ der ~8 KB-Proxy-Cut, der
        // "Path: $.list.data[9] ... reached end of data" auslöste). Nicht-leer → rutschte
        // früher durch die reine Leer-Prüfung.
        c.ChapterList[0].ChapterJsonContent = "{\"list\":{\"name\":\"Ch1\",\"data\":[{\"id\":10},{\"id\":11,\"na";
        Assert.False(RawCourseCache.IsComplete(c));
    }

    [Fact]
    public void IsComplete_TruncatedLineContent_False()
    {
        var c = Complete("{}");
        c.ChapterList[0].ResponseLineList[0].LineJsonContent = "{\"game\":{\"moves\":[{\"san\":\"e4\""; // abgeschnitten
        Assert.False(RawCourseCache.IsComplete(c));
    }

    [Fact]
    public void IsComplete_EmptyButValidChapter_True()
    {
        // Ein legitim leeres Kapitel (parsbares JSON, leeres data-Array) ist KEIN Defekt.
        var c = Complete("{}");
        c.ChapterList[0].ChapterJsonContent = "{\"list\":{\"data\":[]}}";
        Assert.True(RawCourseCache.IsComplete(c));
    }

    // --- Packet-Härtung (prod): zu großer (komprimierter) Kurs sprengt max_allowed_packet ---

    [Fact]
    public async Task Set_CompressedExceedsLimit_NotCached()
    {
        // Mini-Cap (1 Byte) erzwingt, dass selbst ein vollständiger Mini-Kurs als "zu groß" gilt
        // → wird übersprungen statt mit "Error submitting NMB packet" zu crashen.
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var cache = new RawCourseCache(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RawCourseCache>.Instance,
            maxCompressedPayloadBytes: 1);

        await cache.SetAsync("toobig", Complete("{\"course\":{\"data\":[]}}"));

        Assert.Null(await cache.GetAsync("toobig")); // Limit überschritten → nicht gecacht
    }

    [Fact]
    public async Task Set_CompressedWithinLimit_IsCached()
    {
        // Gegenprobe: großzügiges Limit → vollständiger Kurs wird normal gecacht.
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var cache = new RawCourseCache(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RawCourseCache>.Instance,
            maxCompressedPayloadBytes: 10 * 1024 * 1024);

        await cache.SetAsync("ok", Complete("{\"course\":{\"data\":[]}}"));

        Assert.NotNull(await cache.GetAsync("ok"));
    }

    // Selbstheilung: ein bereits (vor der Härtung) truncated gecachter Kurs wird beim Lesen
    // erkannt, gelöscht und als Cache-Miss gemeldet → der laufende Import zieht sofort frisch.
    [Fact]
    public async Task GetAsync_TruncatedCachedCourse_DeletedAndReturnsNull()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var cache = new RawCourseCache(scopeFactory, NullLogger<RawCourseCache>.Instance);

        // Vergifteten (truncated) Kurs direkt in die DB legen — SetAsync würde ihn ablehnen.
        var poisoned = Complete("{}");
        poisoned.ChapterList[0].ChapterJsonContent = "{\"list\":{\"data\":[{\"id\":1},{\"id";
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CachedRawCourses.Add(new CachedRawCourse
            {
                Bid = "poison",
                RestResponseJson = GzipBase64(JsonSerializer.Serialize(poisoned)),
                CachedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(await cache.GetAsync("poison")); // korrupt erkannt → Cache-Miss

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.CachedRawCourses.AnyAsync(c => c.Bid == "poison")); // gelöscht
        }
    }

    [Fact]
    public async Task ExistsAsync_CompleteCachedCourse_True()
    {
        var cache = BuildCache();
        await cache.SetAsync("bidOK", Complete("{}"));
        Assert.True(await cache.ExistsAsync("bidOK"));
        Assert.False(await cache.ExistsAsync("nope"));
    }

    // Kern des Parallel-Lauf-Fixes: ein vergifteter (truncated) Cache darf NICHT als „cached"
    // gelten — sonst nimmt rookhub den parallelen Detached-Pfad statt der seriellen Fetch-Queue.
    [Fact]
    public async Task ExistsAsync_TruncatedCachedCourse_False_AndDeleted()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var cache = new RawCourseCache(scopeFactory, NullLogger<RawCourseCache>.Instance);

        var poisoned = Complete("{}");
        poisoned.ChapterList[0].ChapterJsonContent = "{\"list\":{\"data\":[{\"id\":1},{\"id";
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CachedRawCourses.Add(new CachedRawCourse
            {
                Bid = "poison",
                RestResponseJson = GzipBase64(JsonSerializer.Serialize(poisoned)),
                CachedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.False(await cache.ExistsAsync("poison")); // gilt NICHT als cached → serieller Pfad

        using var verifyScope = scopeFactory.CreateScope();
        var vdb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await vdb.CachedRawCourses.AnyAsync(c => c.Bid == "poison")); // selbstheilend gelöscht
    }

    // gzip+Base64 wie RawCourseCache.Compress (privat) — für das direkte Seeden eines Roh-Eintrags.
    private static string GzipBase64(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
            gz.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(output.ToArray());
    }
}
