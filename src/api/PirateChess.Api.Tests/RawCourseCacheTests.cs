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
        ch.ResponseLineList.Add(new RestResponseLine { Oid = 1, LineJsonContent = "{\"game\":{}}" });
        c.ChapterList.Add(ch);
        return c;
    }

    // Kurs mit <usable> verwertbaren + <dead> LEEREN (toten) Linien in einem Kapitel.
    private static RestResponseCourse WithLines(int usable, int dead)
    {
        var c = new RestResponseCourse { CourseJsonContent = "{}" };
        var ch = new RestResponseChapter { ChapterJsonContent = "{\"list\":{}}" };
        for (int i = 0; i < usable; i++)
            ch.ResponseLineList.Add(new RestResponseLine { Oid = i + 1, LineJsonContent = "{\"game\":{}}" });
        for (int i = 0; i < dead; i++)
            ch.ResponseLineList.Add(new RestResponseLine { Oid = 1000 + i, LineJsonContent = "" });
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

    // Wenige tote (leere) Linien in einem sonst vollständigen Kurs werden toleriert → cachebar
    // (früher machte EINE leere Linie den ganzen Kurs uncachebar; bid-116242-Fall).
    [Fact]
    public void IsComplete_FewDeadLines_True()
    {
        Assert.True(RawCourseCache.IsComplete(WithLines(usable: 10, dead: 2)));
    }

    // Über der Toleranzgrenze (Default 5) → weiterhin unvollständig.
    [Fact]
    public void IsComplete_TooManyDeadLines_False()
    {
        Assert.False(RawCourseCache.IsComplete(WithLines(usable: 10, dead: 6)));
    }

    // Auch unterhalb der Grenze: überwiegen die toten Linien, gilt der Kurs als unvollständig
    // (schützt kleine/massiv lückenhafte Kurse vorm „vollständig"-Cachen mit lauter Löchern).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void IsComplete_DeadLinesNotOutnumberedByUsable_False(string lineContent)
    {
        var c = Complete("{}"); // 1 verwertbare Linie
        c.ChapterList[0].ResponseLineList.Add(new RestResponseLine { LineJsonContent = lineContent }); // 1 tote → 1 == 1
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
        var poisoned = WithLines(usable: 2, dead: 6); // 6 tote Linien > Toleranzgrenze → unvollständig

        await cache.SetAsync("bidX", poisoned);

        Assert.Null(await cache.GetAsync("bidX")); // wurde NICHT gecacht
    }

    [Fact]
    public async Task Set_FewDeadLines_IsCachedAndRoundTrips()
    {
        // Kurs mit ein paar toten Linien (Chessable liefert für die oids nichts) wird jetzt gecacht,
        // statt bei jedem Import komplett neu geholt zu werden. Die toten Linien bleiben leere Lücken.
        var cache = BuildCache();

        await cache.SetAsync("bidDead", WithLines(usable: 8, dead: 2));

        var got = await cache.GetAsync("bidDead");
        Assert.NotNull(got);
        Assert.Equal(10, got!.ChapterList[0].ResponseLineList.Count); // 8 gefüllt + 2 leere Lücken
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

    // --- Struktur-statt-Inhalt: Kurs-Blob speichert nur Kapitel + Linien-Oids, Inhalt kommt aus CachedRawLines ---

    [Fact]
    public async Task Set_StoresStructureOnly_AndReconstructsFromLineCache()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var sf = sp.GetRequiredService<IServiceScopeFactory>();
        var cache = new RawCourseCache(sf, NullLogger<RawCourseCache>.Instance);

        var course = Complete("{\"c\":1}");
        course.ChapterList[0].ResponseLineList[0].Oid = 4242;
        course.ChapterList[0].ResponseLineList[0].LineJsonContent = "{\"game\":{\"x\":1}}";
        await cache.SetAsync("bidS", course);

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using (var scope = sf.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Im Blob steht nur die Struktur: Oid gesetzt, Inhalt leer.
            var raw = await db.CachedRawCourses.AsNoTracking().FirstAsync(c => c.Bid == "bidS");
            var stored = JsonSerializer.Deserialize<RestResponseCourse>(GzipText.Decompress(raw.RestResponseJson), opts)!;
            Assert.Equal(4242, stored.ChapterList[0].ResponseLineList[0].Oid);
            Assert.True(string.IsNullOrEmpty(stored.ChapterList[0].ResponseLineList[0].LineJsonContent));
            // Inhalt liegt im per-Oid-Cache.
            Assert.True(await db.CachedRawLines.AnyAsync(l => l.Oid == 4242));
        }

        // GetAsync rekonstruiert den Inhalt aus dem Linien-Cache.
        var got = await cache.GetAsync("bidS");
        Assert.Equal("{\"game\":{\"x\":1}}", got!.ChapterList[0].ResponseLineList[0].LineJsonContent);
        Assert.Equal("{\"c\":1}", got.CourseJsonContent);
    }

    [Fact]
    public async Task Get_StructureWithMissingLine_ReturnsNull()
    {
        // Struktur-Eintrag, dessen referenzierte Linie NICHT im Linien-Cache liegt → unvollständig → Miss.
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var sf = sp.GetRequiredService<IServiceScopeFactory>();
        var cache = new RawCourseCache(sf, NullLogger<RawCourseCache>.Instance);

        var structureOnly = Complete("{}");
        structureOnly.ChapterList[0].ResponseLineList[0].Oid = 9999;
        structureOnly.ChapterList[0].ResponseLineList[0].LineJsonContent = null; // nur Referenz, kein Inhalt
        using (var scope = sf.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CachedRawCourses.Add(new CachedRawCourse
            {
                Bid = "miss",
                RestResponseJson = GzipBase64(JsonSerializer.Serialize(structureOnly)),
                CachedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(await cache.GetAsync("miss")); // Linie 9999 fehlt → IsComplete false → Cache-Miss
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

    // --- Force-Refresh: Cache eines Kurses verwerfen -------------------------
    // Ohne Delete bediente jeder Treffer ewig den Stand des Erst-Imports; ein vom Autor
    // aktualisierter Chessable-Kurs kam nie an.
    [Fact]
    public async Task DeleteAsync_RemovesCourseAndItsLines()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString(); // einmal festlegen → alle Scopes teilen denselben Store
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var cache = new RawCourseCache(scopeFactory, NullLogger<RawCourseCache>.Instance);

        await cache.SetAsync("bid1", Complete("{\"course\":{\"data\":[]}}"));
        Assert.NotNull(await cache.GetAsync("bid1"));

        var (removed, lines) = await cache.DeleteAsync("bid1");

        Assert.True(removed);
        Assert.Equal(1, lines);                       // Linien MÜSSEN mit weg (Resume-Cache!)
        Assert.Null(await cache.GetAsync("bid1"));
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.CachedRawLines.AnyAsync());  // sonst läge der alte Linieninhalt weiter vor
    }

    [Fact]
    public async Task DeleteAsync_UnknownBid_NoOp()
    {
        var cache = BuildCache();
        Assert.Equal((false, 0), await cache.DeleteAsync("nope"));
    }

    // --- Per-Bid-Lock: Refcount-Aufräumen (kein unbegrenzt wachsendes Semaphoren-Wörterbuch) ------

    [Fact]
    public async Task AcquireBidLock_LastHolderRemovesEntry()
    {
        var cache = BuildCache();

        var handles = new List<IDisposable>();
        for (var i = 0; i < 100; i++)
            handles.Add(await cache.AcquireBidLockAsync("bid" + i));
        Assert.Equal(100, cache.ActiveBidLockCount); // während des Haltens leben die Einträge

        foreach (var h in handles) h.Dispose();
        Assert.Equal(0, cache.ActiveBidLockCount); // letzter Halter räumt auf → kein Leck über die Laufzeit
    }

    [Fact]
    public async Task AcquireBidLock_SameBid_IsMutuallyExclusive()
    {
        var cache = BuildCache();

        var first = await cache.AcquireBidLockAsync("bidX");
        var secondTask = cache.AcquireBidLockAsync("bidX");

        // Der zweite Aufrufer darf NICHT durchrutschen, solange der erste hält (kein frisches
        // Semaphor durch verfrühtes Aufräumen — der Warter zählt als Referenz mit).
        var winner = await Task.WhenAny(secondTask, Task.Delay(200));
        Assert.NotSame(secondTask, winner);
        Assert.Equal(1, cache.ActiveBidLockCount); // EIN geteilter Eintrag (Halter + Warter)

        first.Dispose();
        var second = await secondTask; // jetzt kommt der Warter dran
        second.Dispose();
        Assert.Equal(0, cache.ActiveBidLockCount);
    }

    [Fact]
    public async Task AcquireBidLock_CancelledWaiter_ReleasesItsReference()
    {
        var cache = BuildCache();

        var holder = await cache.AcquireBidLockAsync("bidY");
        using var cts = new CancellationTokenSource();
        var waiterTask = cache.AcquireBidLockAsync("bidY", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiterTask);
        holder.Dispose();
        Assert.Equal(0, cache.ActiveBidLockCount); // abgebrochener Warter hinterlässt keinen Eintrag
    }

    [Fact]
    public async Task AcquireBidLock_DoubleDispose_IsHarmless()
    {
        var cache = BuildCache();

        var handle = await cache.AcquireBidLockAsync("bidZ");
        handle.Dispose();
        handle.Dispose(); // idempotent — kein Release-Überschuss, keine Exception
        Assert.Equal(0, cache.ActiveBidLockCount);

        // Lock danach normal wiederverwendbar.
        (await cache.AcquireBidLockAsync("bidZ")).Dispose();
        Assert.Equal(0, cache.ActiveBidLockCount);
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
