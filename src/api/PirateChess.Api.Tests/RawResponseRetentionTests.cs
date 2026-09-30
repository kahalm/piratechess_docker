using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using PirateChess.Api.BackgroundJobs;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class RawResponseRetentionTests
{
    private static AppDbContext NewDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ChessableRawResponse Row(DateTime when)
        => new() { Endpoint = "line", Url = "u", StatusCode = 200, RawJson = "x", DurationMs = 1, RequestedAt = when };

    [Fact]
    public async Task Prune_DeletesOnlyRowsOlderThanCutoff()
    {
        using var db = NewDb();
        var now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        db.ChessableRawResponses.AddRange(
            Row(now.AddDays(-30)),   // alt → weg
            Row(now.AddDays(-15)),   // alt → weg
            Row(now.AddDays(-1)),    // frisch → bleibt
            Row(now));               // frisch → bleibt
        await db.SaveChangesAsync();

        var deleted = await RawResponseRetentionService.PruneOlderThanAsync(db, now.AddDays(-14), 2000);

        Assert.Equal(2, deleted);
        Assert.Equal(2, await db.ChessableRawResponses.CountAsync());
        Assert.True(await db.ChessableRawResponses.AllAsync(r => r.RequestedAt >= now.AddDays(-14)));
    }

    [Fact]
    public async Task Prune_BatchesAcrossMultipleSaves()
    {
        using var db = NewDb();
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 50; i++) db.ChessableRawResponses.Add(Row(old));
        await db.SaveChangesAsync();

        // Batchgröße 7 < 50 → mehrere Durchläufe, am Ende alles weg.
        var deleted = await RawResponseRetentionService.PruneOlderThanAsync(db, old.AddDays(1), 7);

        Assert.Equal(50, deleted);
        Assert.Equal(0, await db.ChessableRawResponses.CountAsync());
    }

    [Fact]
    public async Task Prune_NothingToDelete_ReturnsZero()
    {
        using var db = NewDb();
        var now = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        db.ChessableRawResponses.Add(Row(now));
        await db.SaveChangesAsync();

        Assert.Equal(0, await RawResponseRetentionService.PruneOlderThanAsync(db, now.AddDays(-14), 2000));
        Assert.Equal(1, await db.ChessableRawResponses.CountAsync());
    }

    /// <summary>Zählt jede aus einem Abfrageergebnis materialisierte <see cref="ChessableRawResponse"/>.</summary>
    private sealed class RawRowMaterializationCounter : IMaterializationInterceptor
    {
        public int Count;

        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (entity is ChessableRawResponse) Count++;
            return entity;
        }
    }

    // S2-019: die Retention lud je Batch 2 000 ganze Zeilen samt RawJson (bis ~170 KB Base64 je Zeile) in den Heap,
    // nur um sie zu löschen. Jetzt: nur Ids, gelöscht über Stub-Entities — keine Zeile wird materialisiert.
    [Fact]
    public async Task Prune_NeverMaterializesRawRows_OnlyIds()
    {
        // Eigene Wurzel: der Kontext mit Interceptor bekommt einen anderen internen Service-Provider und sähe ohne
        // gemeinsame Wurzel eine leere Datenbank gleichen Namens.
        var name = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var seed = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                   .UseInMemoryDatabase(name, root).Options))
        {
            for (var i = 0; i < 10; i++) seed.ChessableRawResponses.Add(Row(old));
            seed.ChessableRawResponses.Add(Row(old.AddDays(30)));   // frisch → bleibt
            await seed.SaveChangesAsync();
        }

        var counter = new RawRowMaterializationCounter();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name, root).AddInterceptors(counter).Options);

        var deleted = await RawResponseRetentionService.PruneOlderThanAsync(db, old.AddDays(1), 4);

        Assert.Equal(10, deleted);
        Assert.Equal(0, counter.Count);
        Assert.Equal(1, await db.ChessableRawResponses.AsNoTracking().CountAsync());
    }

    [Fact]
    public void GzipText_RoundTrips_AndShrinksRepetitiveJson()
    {
        var json = "{\"game\":{\"moves\":[" + string.Concat(Enumerable.Repeat("{\"san\":\"e4\"},", 500)) + "]}}";

        var compressed = GzipText.Compress(json);

        Assert.Equal(json, GzipText.Decompress(compressed));
        Assert.True(compressed.Length < json.Length); // gut komprimierbar
    }
}
