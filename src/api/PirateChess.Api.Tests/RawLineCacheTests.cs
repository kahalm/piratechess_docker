using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class RawLineCacheTests
{
    // Eine getGame-Antwort, wie der Parser sie liest (gleiche Form wie in BrowserParseCacheTests).
    private static string Line(string san = "e4", string ann = "")
        => "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"col\":\"w\",\"san\":\"" + san + "\",\"ann1\":\"" + ann + "\"}]}}";

    // Abgeschnitten wie oid 36114125 (Kurs 207313, im Cache seit 2026-06-14): das JSON bricht mitten in einem Text ab.
    private const string Truncated =
        "{\"game\":{\"owned\":true,\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"col\":\"w\",\"san\":\"e4\",\"ann1\":\"Developing the kni";

    private static (RawLineCache Cache, IServiceProvider Services) BuildCache()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString(); // einmal festlegen → alle Scopes teilen denselben Store
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        return (new RawLineCache(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RawLineCache>.Instance), sp);
    }

    private static async Task<CachedRawLine?> RowAsync(IServiceProvider sp, int oid)
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().CachedRawLines.AsNoTracking().FirstOrDefaultAsync(c => c.Oid == oid);
    }

    private static async Task<List<CachedRawLineArchive>> ArchiveAsync(IServiceProvider sp, int oid)
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().CachedRawLineArchive.AsNoTracking().Where(a => a.Oid == oid).ToListAsync();
    }

    private static async Task SeedAsync(IServiceProvider sp, int oid, string json, DateTime? invalidAt = null, string? reason = null)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CachedRawLines.Add(new CachedRawLine
        {
            Oid = oid, LineJsonContent = GzipText.Compress(json), CachedAt = new DateTime(2026, 6, 14, 18, 35, 34, DateTimeKind.Utc),
            InvalidAt = invalidAt, InvalidReason = reason,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SetThenGet_ByOid_RoundTrips()
    {
        var (cache, _) = BuildCache();

        await cache.SetAsync(12345, Line());

        Assert.Equal(Line(), await cache.GetAsync(12345));
        Assert.Null(await cache.GetAsync(99999)); // andere Linie → kein Treffer
    }

    [Fact]
    public async Task Set_Twice_UpdatesSameOid()
    {
        var (cache, _) = BuildCache();
        await cache.SetAsync(7, Line("e4"));
        await cache.SetAsync(7, Line("d4"));

        Assert.Equal(Line("d4"), await cache.GetAsync(7));
    }

    [Fact]
    public async Task Get_LargeContent_RoundTripsViaGzip()
    {
        var (cache, _) = BuildCache();
        var big = Line(ann: new string('a', 200_000)); // einzelne Linien können groß sein
        await cache.SetAsync(42, big);

        Assert.Equal(big, await cache.GetAsync(42));
    }

    // --- Cache-Härtung: leere / {}-Antworten NIE cachen (kein vergifteter Resume) ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void IsComplete_EmptyOrBlock_False(string content)
        => Assert.False(RawLineCache.IsComplete(content));

    [Fact]
    public void IsComplete_RealContent_True()
        => Assert.True(RawLineCache.IsComplete("""{"game":{}}"""));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public async Task Set_EmptyOrBlock_NotCached(string content)
    {
        var (cache, sp) = BuildCache();
        await cache.SetAsync(555, content);
        Assert.Null(await cache.GetAsync(555)); // wurde NICHT gecacht
        Assert.Null(await RowAsync(sp, 555));
    }

    // --- Ungültige Linien: markieren, nie löschen, nie verwenden ---

    [Fact]
    public void InvalidReason_NamesTheProblem_OrNullForAUsableLine()
    {
        Assert.Null(RawLineCache.InvalidReason(Line()));
        Assert.StartsWith("JSON:", RawLineCache.InvalidReason(Truncated));
        Assert.Equal("kein game-Objekt", RawLineCache.InvalidReason("""{"x":1}"""));
        Assert.Equal("leer", RawLineCache.InvalidReason("{}"));
    }

    [Fact]
    public async Task Set_TruncatedLine_IsKeptMarked_ButNeverServed()
    {
        var (cache, sp) = BuildCache();

        await cache.SetAsync(36114125, Truncated);

        Assert.Null(await cache.GetAsync(36114125));
        var row = await RowAsync(sp, 36114125);
        Assert.NotNull(row);
        Assert.NotNull(row!.InvalidAt);
        Assert.StartsWith("JSON:", row.InvalidReason);
        Assert.Equal(Truncated, GzipText.Decompress(row.LineJsonContent));   // Inhalt bleibt erhalten
        Assert.Empty(await cache.GetCachedOidsAsync([36114125]));
    }

    [Fact]
    public async Task Set_TruncatedLine_NeverReplacesAValidOne()
    {
        var (cache, sp) = BuildCache();
        await cache.SetAsync(8, Line("c4"));

        await cache.SetAsync(8, Truncated);

        Assert.Equal(Line("c4"), await cache.GetAsync(8));
        Assert.Null((await RowAsync(sp, 8))!.InvalidAt);
    }

    [Fact]
    public async Task Get_StoredLineTurnsOutTruncated_IsMarked_AndContentKept()
    {
        var (cache, sp) = BuildCache();
        await SeedAsync(sp, 36114125, Truncated);   // Altbestand: vor der Prüfung ungeprüft abgelegt

        Assert.Null(await cache.GetAsync(36114125));

        var row = await RowAsync(sp, 36114125);
        Assert.NotNull(row!.InvalidAt);
        Assert.Equal(Truncated, GzipText.Decompress(row.LineJsonContent));
    }

    [Fact]
    public async Task GetMany_LeavesOutAndMarksTheTruncatedLine_ReturnsTheRest()
    {
        var (cache, sp) = BuildCache();
        await SeedAsync(sp, 1, Line("e4"));
        await SeedAsync(sp, 36114125, Truncated);
        await SeedAsync(sp, 3, Line("d4"));

        var lines = await cache.GetManyAsync([1, 36114125, 3]);

        Assert.Equal(new[] { 1, 3 }, lines.Keys.Order());
        Assert.NotNull((await RowAsync(sp, 36114125))!.InvalidAt);
        // und die Extension bekommt die Linie ab jetzt nicht mehr als „gecacht" gemeldet → sie holt sie selbst
        Assert.Equal(new[] { 1, 3 }, (await cache.GetCachedOidsAsync([1, 36114125, 3])).Order());
    }

    [Fact]
    public async Task Set_ValidLine_HealsAMarkedLine_AndArchivesTheOldContent()
    {
        var (cache, sp) = BuildCache();
        await SeedAsync(sp, 36114125, Truncated, DateTime.UtcNow, "JSON: abgeschnitten");

        await cache.SetAsync(36114125, Line("e4", "Developing the knight"));

        Assert.Equal(Line("e4", "Developing the knight"), await cache.GetAsync(36114125));
        var row = await RowAsync(sp, 36114125);
        Assert.Null(row!.InvalidAt);
        Assert.Null(row.InvalidReason);
        var archived = Assert.Single(await ArchiveAsync(sp, 36114125));
        Assert.Equal(Truncated, GzipText.Decompress(archived.LineJsonContent));
        Assert.Equal("JSON: abgeschnitten", archived.InvalidReason);
    }

    [Fact]
    public async Task AddMissing_HealsAMarkedLine_ButNeverTouchesAValidOne()
    {
        var (cache, sp) = BuildCache();
        await SeedAsync(sp, 36114125, Truncated, DateTime.UtcNow, "JSON: abgeschnitten");
        await SeedAsync(sp, 2, Line("c4"));

        var added = await cache.AddMissingAsync(new Dictionary<int, string>
        {
            [36114125] = Line("e4"),
            [2] = Line("g3"),
            [4] = Line("b3"),
        });

        Assert.Equal(2, added);   // geheilt + neu, die gültige 2 bleibt
        Assert.Equal(Line("e4"), await cache.GetAsync(36114125));
        Assert.Equal(Line("c4"), await cache.GetAsync(2));
        Assert.Equal(Line("b3"), await cache.GetAsync(4));
        Assert.Single(await ArchiveAsync(sp, 36114125));
        Assert.Empty(await ArchiveAsync(sp, 2));
    }

    [Fact]
    public async Task Revalidate_DryRun_ReportsButWritesNothing()
    {
        var (cache, sp) = BuildCache();
        await SeedAsync(sp, 10, Line());
        await SeedAsync(sp, 11, Truncated);

        var r = await cache.RevalidateAsync(all: true, apply: false);

        Assert.Equal(2, r.Checked);
        Assert.Equal(1, r.NewlyInvalidCount);
        Assert.Equal(11, Assert.Single(r.NewlyInvalid).Oid);
        Assert.Null((await RowAsync(sp, 11))!.InvalidAt);
    }

    [Fact]
    public async Task Revalidate_Apply_MarksBrokenLines_AndReleasesMarkedLinesThatNowPass()
    {
        var (cache, sp) = BuildCache();
        await SeedAsync(sp, 20, Line());
        await SeedAsync(sp, 21, Truncated);
        // markiert unter einer früheren, zu strengen Prüfung — der Inhalt ist in Ordnung
        await SeedAsync(sp, 22, Line("d4"), DateTime.UtcNow, "alte Prüfung");
        await SeedAsync(sp, 23, Truncated, DateTime.UtcNow, "JSON: abgeschnitten");

        var onlyMarked = await cache.RevalidateAsync(all: false, apply: false);
        Assert.Equal(2, onlyMarked.Checked);   // ohne all nur die markierten

        var r = await cache.RevalidateAsync(all: true, apply: true);

        Assert.Equal(4, r.Checked);
        Assert.Equal(new[] { 21 }, r.NewlyInvalid.Select(x => x.Oid));
        Assert.Equal(new[] { 22 }, r.Cleared);
        Assert.Equal(1, r.StillInvalid);
        Assert.NotNull((await RowAsync(sp, 21))!.InvalidAt);
        Assert.Equal(Line("d4"), await cache.GetAsync(22));
        Assert.NotNull((await RowAsync(sp, 23))!.InvalidAt);
    }
}
