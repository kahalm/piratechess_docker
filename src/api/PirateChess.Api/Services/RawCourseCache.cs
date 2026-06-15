using System.IO.Compression;
using System.Text;
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
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Obergrenze für einen einzelnen Cache-Eintrag (komprimierte Base64-Länge ≈ Paket-Bytes).
    /// Muss unter MariaDBs <c>max_allowed_packet</c> (Prod/Dev: 256 MB) bleiben — sonst scheitert
    /// der INSERT mit "Error submitting NMB packet". Default 200 MB lässt Reserve fürs Statement.
    /// </summary>
    public const int DefaultMaxCompressedPayloadBytes = 200 * 1024 * 1024;

    public RawCourseCache(
        IServiceScopeFactory scopeFactory,
        ILogger<RawCourseCache> logger,
        int maxCompressedPayloadBytes = DefaultMaxCompressedPayloadBytes)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _maxCompressedPayloadBytes = maxCompressedPayloadBytes;
    }

    public async Task<RestResponseCourse?> GetAsync(string bid, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CachedRawCourses.AsNoTracking().FirstOrDefaultAsync(c => c.Bid == bid, ct);
            if (row is null || string.IsNullOrEmpty(row.RestResponseJson)) return null;
            var json = Decompress(row.RestResponseJson);
            var course = JsonSerializer.Deserialize<RestResponseCourse>(json, JsonOpts);

            // Selbstheilung: ein (vor dieser Härtung) truncated gecachter Kurs würde sonst jeden
            // Import erneut crashen lassen. Beim Lesen prüfen und einen korrupten Eintrag SOFORT
            // löschen + null liefern → der laufende Import sieht einen Cache-Miss und holt die
            // Daten gleich frisch von Chessable (Linien kommen dank RawLineCache aus dem Resume-Cache).
            if (course is not null && !IsComplete(course))
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
    public async Task<bool> ExistsAsync(string bid, CancellationToken ct = default)
        => await GetAsync(bid, ct) is not null;

    /// <summary>
    /// Ein Kurs gilt als vollständig (und damit cache-würdig), wenn er mind. ein Kapitel hat
    /// und KEIN Kapitel/keine Linie leeren bzw. <c>{}</c>-Roh-Content trägt. Ein Teil-Fetch
    /// (z.B. Linie nach 10 erfolglosen Retries als "" abgelegt) darf NICHT gecacht werden —
    /// sonst vergiftet er jeden Replay: leerer Content lässt die PGN-Generierung scheitern
    /// bzw. erzeugt lückenhafte Kurse. (Genau das war der bid-116242-Dauerfehler.)
    /// </summary>
    public static bool IsComplete(RestResponseCourse? course)
    {
        if (course?.ChapterList is null || course.ChapterList.Count == 0)
            return false;
        foreach (var ch in course.ChapterList)
        {
            if (string.IsNullOrWhiteSpace(ch.ChapterJsonContent) || ch.ChapterJsonContent == "{}")
                return false;
            // Truncated/korruptes Kapitel-JSON (nicht-leer, aber unvollständig — z.B. ~8 KB-Cut
            // durch den VPN-Proxy) erkennen: muss vollständig als ResponseChapter parsen, sonst
            // ist es ein vergifteter Teil-Fetch → nicht cachen (bzw. beim Lesen verwerfen).
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
                if (string.IsNullOrWhiteSpace(ln.LineJsonContent) || ln.LineJsonContent == "{}")
                    return false;
                // Symmetrisch zum Kapitel: abgeschnittene/korrupte Linien-JSON ebenfalls als
                // unvollständig werten (nicht cachen / beim Lesen verwerfen → sofort neu holen).
                try
                {
                    if (JsonSerializer.Deserialize<ResponseLine>(ln.LineJsonContent, JsonOpts) is null)
                        return false;
                }
                catch (JsonException)
                {
                    return false;
                }
            }
        }
        return true;
    }

    public async Task SetAsync(string bid, RestResponseCourse course, CancellationToken ct = default)
    {
        if (!IsComplete(course))
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
            var compressed = Compress(JsonSerializer.Serialize(course));
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

    /// <summary>gzip + Base64 — schrumpft das große Kurs-JSON deutlich (gut komprimierbar).</summary>
    private static string Compress(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal))
            gz.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(output.ToArray());
    }

    private static string Decompress(string base64)
    {
        var data = Convert.FromBase64String(base64);
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
