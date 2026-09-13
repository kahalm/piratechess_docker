using Microsoft.EntityFrameworkCore;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;

namespace PirateChess.Api.Services;

/// <summary>
/// Persistenter, zeilen-(oid-)basierter Resume-Cache der rohen getGame-Antwort einer Kurs-Linie.
/// Chessable-Linien-IDs (oid) sind global eindeutig und der Inhalt ist user-/kursunabhängig →
/// eine einmal erfolgreich geholte Linie muss bei einem (Neu-)Start NICHT erneut bei Chessable
/// abgefragt werden. Bricht ein Kursabruf in der Mitte ab, holt der Neustart nur die fehlenden
/// Linien. Überlebt Neustarts (DB). Cache-Fehler sind nie fatal (dann wird eben neu geholt).
///
/// Es werden NUR erfolgreiche Antworten gecacht (nie leer / "{}") — analog zum
/// <see cref="RawCourseCache"/>-Härtungsprinzip: ein leerer Roh-Content würde sonst jeden Replay
/// vergiften. Per-Linie ist das unkritisch, weil wir hier ausschließlich Erfolge ablegen.
///
/// Roh-Content kann groß sein (einzelne Linien &gt;500 KB) → gzip+Base64, wie der Kurs-Cache.
/// </summary>
public class RawLineCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RawLineCache> _logger;

    public RawLineCache(IServiceScopeFactory scopeFactory, ILogger<RawLineCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Eine Linie ist cache-würdig, wenn ihr Roh-Content nicht leer und nicht <c>{}</c> ist.</summary>
    public static bool IsComplete(string? content)
        => !string.IsNullOrWhiteSpace(content) && content != "{}";

    /// <summary>Liefert den gecachten Roh-Content der Linie (oid) oder null, wenn nicht vorhanden.</summary>
    public async Task<string?> GetAsync(int oid, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CachedRawLines.AsNoTracking().FirstOrDefaultAsync(c => c.Oid == oid, ct);
            if (row is null || string.IsNullOrEmpty(row.LineJsonContent)) return null;
            return GzipText.Decompress(row.LineJsonContent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawLineCache.Get fehlgeschlagen für oid {Oid}", oid);
            return null;
        }
    }

    /// <summary>Legt eine erfolgreiche Linie ab (Upsert). Leere/<c>{}</c>-Antworten werden ignoriert.</summary>
    public async Task SetAsync(int oid, string content, CancellationToken ct = default)
    {
        if (!IsComplete(content))
            return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var compressed = GzipText.Compress(content);
            var row = await db.CachedRawLines.FirstOrDefaultAsync(c => c.Oid == oid, ct);
            if (row is null)
                db.CachedRawLines.Add(new CachedRawLine { Oid = oid, LineJsonContent = compressed, CachedAt = DateTime.UtcNow });
            else
            {
                row.LineJsonContent = compressed;
                row.CachedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawLineCache.Set fehlgeschlagen für oid {Oid}", oid);
        }
    }

    /// <summary>Welche der oids liegen im Cache — nur die Existenz, kein Inhalt (gebatcht).</summary>
    public async Task<HashSet<int>> GetCachedOidsAsync(IEnumerable<int> oids, CancellationToken ct = default)
    {
        var result = new HashSet<int>();
        var wanted = oids.Where(o => o > 0).Distinct().ToList();
        if (wanted.Count == 0) return result;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var chunk in wanted.Chunk(1000))
                result.UnionWith(await db.CachedRawLines.AsNoTracking()
                    .Where(c => chunk.Contains(c.Oid) && c.LineJsonContent != null && c.LineJsonContent != "")
                    .Select(c => c.Oid)
                    .ToListAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RawLineCache.GetCachedOids fehlgeschlagen für {Count} oids", wanted.Count);
        }
        return result;
    }

    /// <summary>Inhalte mehrerer Linien auf einmal (gebatcht, dekomprimiert). Nicht gecachte fehlen im Ergebnis.</summary>
    public async Task<Dictionary<int, string>> GetManyAsync(IEnumerable<int> oids, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        var wanted = oids.Where(o => o > 0).Distinct().ToList();
        if (wanted.Count == 0) return result;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var chunk in wanted.Chunk(1000))
            {
                var rows = await db.CachedRawLines.AsNoTracking()
                    .Where(c => chunk.Contains(c.Oid))
                    .Select(c => new { c.Oid, c.LineJsonContent })
                    .ToListAsync(ct);
                foreach (var row in rows)
                    if (!string.IsNullOrEmpty(row.LineJsonContent))
                        result[row.Oid] = GzipText.Decompress(row.LineJsonContent);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RawLineCache.GetMany fehlgeschlagen für {Count} oids", wanted.Count);
        }
        return result;
    }

    /// <summary>
    /// Legt Linien ab, die NOCH NICHT im Cache liegen, und liefert deren Anzahl. Vorhandene Einträge werden nie
    /// überschrieben: diese Linien liefert der Browser eines Nutzers, und der erste Stand — meist vom Server
    /// selbst geholt — bleibt maßgeblich (ersetzen kann ihn nur der Force-Refresh des Server-Abrufs).
    /// Inhalte ohne <c>game</c>-Objekt werden übergangen.
    /// </summary>
    public async Task<int> AddMissingAsync(IReadOnlyDictionary<int, string> lines, CancellationToken ct = default)
    {
        var candidates = lines.Where(kv => kv.Key > 0 && BrowserCourseAssembler.IsCacheableLine(kv.Value)).ToList();
        if (candidates.Count == 0) return 0;
        var added = 0;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // In Häppchen speichern: Linien können groß sein, ein einziger INSERT eines ganzen Kurses
            // käme MariaDBs max_allowed_packet zu nahe.
            foreach (var chunk in candidates.Chunk(200))
            {
                var chunkOids = chunk.Select(c => c.Key).ToList();
                var existing = (await db.CachedRawLines.AsNoTracking()
                    .Where(c => chunkOids.Contains(c.Oid)).Select(c => c.Oid).ToListAsync(ct)).ToHashSet();
                var toAdd = chunk.Where(c => !existing.Contains(c.Key))
                    .Select(c => new CachedRawLine { Oid = c.Key, LineJsonContent = GzipText.Compress(c.Value), CachedAt = DateTime.UtcNow })
                    .ToList();
                if (toAdd.Count == 0) continue;
                db.CachedRawLines.AddRange(toAdd);
                await db.SaveChangesAsync(ct);
                added += toAdd.Count;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // z. B. derselbe Kurs parallel von zwei Browsern — der Cache ist nie fatal.
            _logger.LogWarning(ex, "RawLineCache.AddMissing fehlgeschlagen nach {Added} von {Count} Linien", added, candidates.Count);
        }
        return added;
    }
}
