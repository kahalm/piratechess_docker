using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using piratechess_lib;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;

namespace PirateChess.Api.Services;

/// <summary>
/// Persistenter, kurs-(bid-)basierter Cache der rohen Kursstruktur (<see cref="RestResponseCourse"/>).
/// Der Kursinhalt ist für alle Besitzer identisch → ein zweiter User kann denselben Kurs importieren,
/// OHNE dass Chessable erneut abgefragt wird. Überlebt Neustarts (DB). Cache-Fehler sind nie fatal
/// (dann wird eben neu geholt).
///
/// Die Rohdaten können sehr groß sein (Kurse mit 36+ MB JSON) → werden gzip-komprimiert (Base64)
/// gespeichert, damit sie unter MariaDBs max_allowed_packet bleiben.
/// </summary>
public class RawCourseCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RawCourseCache> _logger;
    private readonly int _maxCompressedPayloadBytes;
    private readonly int _maxUnusableLines;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Wie viele LEERE Linien (Chessable liefert für die oid nichts — nach 10 Retries aufgegeben, i.d.R.
    /// eine aus dem Kurs entfernte, aber noch gelistete Linie) ein sonst vollständiger Kurs haben darf und
    /// trotzdem gecacht wird. So bleibt ein Kurs mit ein, zwei toten Linien (z. B. bid 116242) cachebar,
    /// statt bei jedem Import komplett neu von Chessable geholt zu werden. Abgeschnittene (nicht-leere,
    /// unparsbare) Linien und fehlende Kapitel bleiben davon UNBERÜHRT hart „unvollständig" (transienter
    /// Proxy-Cut → soll frisch geholt werden).
    /// </summary>
    public const int DefaultMaxUnusableLines = 5;

    /// <summary>
    /// Obergrenze für einen einzelnen Cache-Eintrag (komprimierte Base64-Länge ≈ Paket-Bytes).
    /// Muss unter MariaDBs <c>max_allowed_packet</c> (Prod/Dev: 256 MB) bleiben — sonst scheitert
    /// der INSERT mit "Error submitting NMB packet". Default 200 MB lässt Reserve fürs Statement.
    /// </summary>
    public const int DefaultMaxCompressedPayloadBytes = 200 * 1024 * 1024;

    public RawCourseCache(
        IServiceScopeFactory scopeFactory,
        ILogger<RawCourseCache> logger,
        int maxCompressedPayloadBytes = DefaultMaxCompressedPayloadBytes,
        int maxUnusableLines = DefaultMaxUnusableLines)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _maxCompressedPayloadBytes = maxCompressedPayloadBytes;
        _maxUnusableLines = maxUnusableLines;
    }

    public async Task<RestResponseCourse?> GetAsync(string bid, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CachedRawCourses.AsNoTracking().FirstOrDefaultAsync(c => c.Bid == bid, ct);
            if (row is null || string.IsNullOrEmpty(row.RestResponseJson)) return null;
            var json = GzipText.Decompress(row.RestResponseJson);
            var course = JsonSerializer.Deserialize<RestResponseCourse>(json, JsonOpts);

            // Struktur-Format: Linieninhalte stehen nicht im Kurs-Blob, sondern je Oid in CachedRawLines.
            // Vor der Vollständigkeitsprüfung nachfüllen (fehlt eine Linie, bleibt sie leer → IsComplete
            // schlägt fehl → Selbstheilung unten greift wie bei einem truncated Eintrag).
            if (course is not null)
                await ReconstructLinesAsync(db, course, ct);

            // Selbstheilung: ein (vor dieser Härtung) truncated gecachter Kurs würde sonst jeden
            // Import erneut crashen lassen. Beim Lesen prüfen und einen korrupten Eintrag SOFORT
            // löschen + null liefern → der laufende Import sieht einen Cache-Miss und holt die
            // Daten gleich frisch von Chessable (Linien kommen dank RawLineCache aus dem Resume-Cache).
            if (course is not null && !IsComplete(course, _maxUnusableLines))
            {
                _logger.LogWarning(
                    "RawCourseCache: gecachter Kurs bid {Bid} ist unvollständig/korrupt (truncated Kapitel) — Eintrag wird gelöscht und sofort frisch geholt",
                    bid);
                var stale = await db.CachedRawCourses.FirstOrDefaultAsync(c => c.Bid == bid, ct);
                if (stale is not null)
                {
                    db.CachedRawCourses.Remove(stale);
                    await db.SaveChangesAsync(ct);
                }
                return null;
            }
            return course;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawCourseCache.Get fehlgeschlagen für bid {Bid}", bid);
            return null;
        }
    }

    /// <summary>
    /// True nur, wenn ein VOLLSTÄNDIG verwertbarer Cache für den Kurs vorliegt. Delegiert an
    /// <see cref="GetAsync"/> → ein unvollständiger/truncated Eintrag liefert dort null (und wird
    /// dabei selbstheilend gelöscht). Wichtig: rookhub entscheidet anhand dieses Checks zwischen
    /// dem seriellen Fetch-Queue-Pfad und dem sofortigen (parallelen) Detached-Pfad. Würde hier
    /// ein vergifteter Eintrag als „cached" gelten, liefe der eigentlich nötige Chessable-Abruf
    /// am seriellen Pfad vorbei → mehrere Kurse zögen parallel über dieselbe VPN-IP.
    /// (Lädt/dekomprimiert die Rohdaten — passiert nur einmal pro Import-Start, kein Hot-Path.)
    /// </summary>
    // ExistsAsync nutzt BEWUSST GetAsync (lädt+dekomprimiert+prüft Vollständigkeit + heilt einen
    // truncated Cache), NICHT ein billiges AnyAsync: ein unvollständiger Cache darf NICHT als
    // „cached" gelten (sonst überspringt der Import den nötigen Re-Fetch). Siehe Test
    // ExistsAsync_TruncatedCachedCourse_False_AndDeleted + Parallel-Lauf-Fix.
    public async Task<bool> ExistsAsync(string bid, CancellationToken ct = default)
        => await GetAsync(bid, ct) is not null;

    // Per-Bid-Lock, damit nicht zwei gleichzeitige Cache-Misses desselben Kurses BEIDE über die (eine)
    // VPN-IP fetchen (verdoppelte Last → höhere Chessable-Block-Rate). Aufrufer: Lock holen, Cache
    // ERNEUT prüfen (double-checked), nur bei weiterhin Miss fetchen. Ein Eintrag je bekanntem bid
    // (Kurs-Katalog ist klein) → vernachlässigbarer, dauerhafter Speicher.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _bidLocks = new();

    /// <summary>Liefert das (geteilte) Lock-Objekt für einen bid — für den Miss→Fetch→Set-Pfad.</summary>
    public SemaphoreSlim BidLock(string bid) => _bidLocks.GetOrAdd(bid, _ => new SemaphoreSlim(1, 1));

    /// <summary>Die konfigurierte Lücken-Toleranz dieser Instanz — damit Vorab-Prüfungen außerhalb
    /// (z. B. RawCourseReconstructor) mit DERSELBEN Toleranz prüfen wie SetAsync/GetAsync, statt
    /// stillschweigend den statischen Default zu verwenden.</summary>
    public int MaxUnusableLines => _maxUnusableLines;

    /// <summary>
    /// Alle gecachten Kurs-Bids auf einen Schlag (für rookhub, um eine Kursliste mit einem
    /// „gecacht"-Flag anzureichern — 1 Call statt N <see cref="ExistsAsync"/>). Bewusst KEINE
    /// Vollständigkeitsprüfung pro Eintrag (zu teuer, würde alle Blobs dekomprimieren); ein evtl.
    /// truncated Eintrag heilt sich beim nächsten echten Abruf via <see cref="GetAsync"/> selbst.
    /// </summary>
    public async Task<HashSet<string>> GetAllCachedBidsAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bids = await db.CachedRawCourses.AsNoTracking().Select(c => c.Bid).ToListAsync(ct);
            return new HashSet<string>(bids);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawCourseCache.GetAllCachedBidsAsync fehlgeschlagen");
            return new HashSet<string>();
        }
    }

    /// <summary>
    /// Ein Kurs gilt als vollständig (und damit cache-würdig), wenn (a) jedes Kapitel vollständig da ist
    /// und (b) höchstens <paramref name="maxUnusableLines"/> Linien LEER sind (und die verwertbaren klar
    /// überwiegen). Hintergrund: manche Kurse listen oids, für die Chessable dauerhaft nichts liefert
    /// (aus dem Kurs entfernte Linien) — der Fetch gibt nach 10 Retries auf und legt "" ab. Früher machte
    /// EINE solche tote Linie den GANZEN Kurs uncachebar (bid-116242-Dauerfehler) → er wurde bei jedem
    /// Import komplett neu von Chessable geholt. Jetzt werden wenige tote Linien toleriert und als Lücke
    /// mitgecacht; ABGESCHNITTENE (nicht-leere, unparsbare) Linien und fehlende/kaputte Kapitel bleiben
    /// hart „unvollständig" (transienter Proxy-Cut → soll frisch geholt werden, nicht als Lücke zementiert).
    /// </summary>
    public static bool IsComplete(RestResponseCourse? course, int maxUnusableLines = DefaultMaxUnusableLines)
    {
        if (course?.ChapterList is null || course.ChapterList.Count == 0)
            return false;
        int usable = 0, unusable = 0;
        foreach (var ch in course.ChapterList)
        {
            // Kapitel müssen IMMER vollständig da sein — ein fehlendes/abgeschnittenes Kapitel ist ein
            // echtes Truncation-Problem (nicht bloß eine tote Einzel-Linie) und macht den Kurs uncachebar.
            if (string.IsNullOrWhiteSpace(ch.ChapterJsonContent) || ch.ChapterJsonContent == "{}")
                return false;
            try
            {
                if (JsonSerializer.Deserialize<ResponseChapter>(ch.ChapterJsonContent, JsonOpts) is null)
                    return false;
            }
            catch (JsonException)
            {
                return false;
            }
            if (ch.ResponseLineList is null)
                continue;
            foreach (var ln in ch.ResponseLineList)
            {
                // Leere/{}-Linie = Chessable liefert für diese oid nichts (tote/entfernte Linie) → als
                // Lücke zählen und bis zur Obergrenze tolerieren.
                if (string.IsNullOrWhiteSpace(ln.LineJsonContent) || ln.LineJsonContent == "{}")
                {
                    unusable++;
                    continue;
                }
                // Nicht-leerer, aber unparsbarer Content = abgeschnitten (Proxy-Cut) → transientes
                // Problem, NICHT tolerieren: der Kurs soll frisch geholt werden, bis die Linie ganz ankommt.
                try
                {
                    if (JsonSerializer.Deserialize<ResponseLine>(ln.LineJsonContent, JsonOpts) is null)
                        return false;
                }
                catch (JsonException)
                {
                    return false;
                }
                usable++;
            }
        }
        // Wenige tote Linien tolerieren, solange die verwertbaren überwiegen (schützt kleine bzw. massiv
        // unvollständige Kurse davor, mit lauter Lücken fälschlich als „vollständig" gecacht zu werden).
        return unusable <= maxUnusableLines && usable > unusable;
    }

    public async Task SetAsync(string bid, RestResponseCourse course, CancellationToken ct = default)
    {
        if (!IsComplete(course, _maxUnusableLines))
        {
            _logger.LogWarning(
                "RawCourseCache.Set übersprungen für bid {Bid}: Kurs unvollständig (leere/truncated Kapitel oder Linien) — nicht cachen, damit kein vergifteter Cache entsteht",
                bid);
            return;
        }
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Linieninhalte in den per-Oid-Cache spiegeln (idempotent), dann nur die Struktur (Kapitel
            // + Linien-Oids) speichern — die Inhalte liegen pro Oid in CachedRawLines und werden beim
            // Lesen rekonstruiert. Spart den Großteil der Größe (Linien = ~95 %).
            await SeedLinesAsync(db, course, ct);
            var compressed = GzipText.Compress(JsonSerializer.Serialize(ToStructure(course)));
            // Selbst komprimiert sprengen einzelne Riesen-Kurse MariaDBs max_allowed_packet (Prod/Dev
            // 256 MB). Solche Einträge lassen sich nicht persistent cachen → sauber überspringen, statt
            // den INSERT mit "Error submitting NMB packet" crashen zu lassen (was bei jedem Import erneut
            // Error-Logs produzierte). Der Kurs wird dann eben bei jedem Import frisch geholt.
            if (compressed.Length > _maxCompressedPayloadBytes)
            {
                _logger.LogWarning(
                    "RawCourseCache.Set übersprungen für bid {Bid}: komprimierte Rohdaten ({Size} Bytes) überschreiten das Cache-Limit ({Limit} Bytes / max_allowed_packet) — Kurs wird nicht gecacht",
                    bid, compressed.Length, _maxCompressedPayloadBytes);
                return;
            }
            var row = await db.CachedRawCourses.FirstOrDefaultAsync(c => c.Bid == bid, ct);
            if (row is null)
                db.CachedRawCourses.Add(new CachedRawCourse { Bid = bid, RestResponseJson = compressed, CachedAt = DateTime.UtcNow });
            else
            {
                row.RestResponseJson = compressed;
                row.CachedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawCourseCache.Set fehlgeschlagen für bid {Bid}", bid);
        }
    }

    /// <summary>
    /// Struktur-Kopie OHNE Linieninhalt: jede Linie behält nur ihre <see cref="RestResponseLine.Oid"/>,
    /// der <c>LineJsonContent</c> wird weggelassen (kommt beim Lesen aus <c>CachedRawLines</c>). Das
    /// hält den Kurs-Blob klein — die Linieninhalte (~95 % der Größe) liegen ohnehin schon pro Oid im
    /// Linien-Cache. Kapitel-JSON bleibt erhalten (kein separater Kapitel-Cache).
    /// </summary>
    private static RestResponseCourse ToStructure(RestResponseCourse course) => new()
    {
        CourseJsonContent = course.CourseJsonContent,
        ChapterList = course.ChapterList.Select(ch => new RestResponseChapter
        {
            ChapterJsonContent = ch.ChapterJsonContent,
            ResponseLineList = ch.ResponseLineList
                .Select(ln => new RestResponseLine { Oid = ln.Oid, LineJsonContent = null })
                .ToList()
        }).ToList()
    };

    /// <summary>
    /// Spiegelt die Linieninhalte des Kurses in den per-Oid-Cache (<c>CachedRawLines</c>), damit
    /// <see cref="GetAsync"/> sie aus dem Struktur-Blob rekonstruieren kann. In der Praxis hat der
    /// Fetch die Linien bereits gecacht → der Existenz-Check findet alle vor und schreibt nichts
    /// (nur eine günstige Abfrage). Macht den Kurs-Cache aber self-contained (robust, falls eine
    /// Linie beim Fetch nicht im Cache landete).
    /// </summary>
    private static async Task SeedLinesAsync(AppDbContext db, RestResponseCourse course, CancellationToken ct)
    {
        var lines = course.ChapterList
            .SelectMany(ch => ch.ResponseLineList)
            .Where(ln => ln.Oid > 0 && !string.IsNullOrEmpty(ln.LineJsonContent))
            .GroupBy(ln => ln.Oid).Select(g => g.First())
            .ToList();
        if (lines.Count == 0) return;

        var existing = new HashSet<int>();
        foreach (var chunk in lines.Select(l => l.Oid).Chunk(1000))
            existing.UnionWith(await db.CachedRawLines.AsNoTracking()
                .Where(c => chunk.Contains(c.Oid)).Select(c => c.Oid).ToListAsync(ct));

        var toAdd = lines.Where(l => !existing.Contains(l.Oid))
            .Select(l => new CachedRawLine
            {
                Oid = l.Oid,
                LineJsonContent = GzipText.Compress(l.LineJsonContent!),
                CachedAt = DateTime.UtcNow
            }).ToList();
        if (toAdd.Count == 0) return;
        db.CachedRawLines.AddRange(toAdd);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Füllt fehlende Linieninhalte (neues Struktur-Format: <c>Oid &gt; 0</c>, leerer Content) aus
    /// <c>CachedRawLines</c> nach — gebatcht. Alte Voll-Blobs (Content vorhanden / Oid 0) bleiben
    /// unangetastet → abwärtskompatibel.
    /// </summary>
    private static async Task ReconstructLinesAsync(AppDbContext db, RestResponseCourse course, CancellationToken ct)
    {
        var missing = course.ChapterList
            .SelectMany(ch => ch.ResponseLineList)
            .Where(ln => ln.Oid > 0 && string.IsNullOrEmpty(ln.LineJsonContent))
            .Select(ln => ln.Oid).Distinct().ToList();
        if (missing.Count == 0) return;

        var byOid = new Dictionary<int, string>();
        foreach (var chunk in missing.Chunk(1000))
        {
            var rows = await db.CachedRawLines.AsNoTracking()
                .Where(c => chunk.Contains(c.Oid))
                .Select(c => new { c.Oid, c.LineJsonContent })
                .ToListAsync(ct);
            foreach (var r in rows)
                if (!string.IsNullOrEmpty(r.LineJsonContent))
                    byOid[r.Oid] = GzipText.Decompress(r.LineJsonContent);
        }

        foreach (var ln in course.ChapterList.SelectMany(ch => ch.ResponseLineList))
            if (ln.Oid > 0 && string.IsNullOrEmpty(ln.LineJsonContent) && byOid.TryGetValue(ln.Oid, out var content))
                ln.LineJsonContent = content;
    }
}
