using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using piratechess_lib;
using PirateChess.Api.Data;

namespace PirateChess.Api.Services;

/// <summary>
/// Baut den servable <see cref="RawCourseCache"/>-Eintrag eines Kurses aus BEREITS GESPEICHERTEN
/// Rohdaten wieder auf — OHNE Chessable-Abruf:
/// <list type="bullet">
///   <item>getCourse-Struktur + Kapitel (getList) aus dem Audit-Log <c>ChessableRawResponses</c>
///     (14-Tage-Retention),</item>
///   <item>Linien-Inhalte aus dem PERMANENTEN Linien-Cache <c>CachedRawLines</c> (Fallback:
///     <c>line</c>-Audit).</item>
/// </list>
/// Gedacht für Kurse, deren aktueller Bearer-Account sie nicht (mehr) besitzt (BOOK_NOT_OWNED), deren
/// Rohantworten aber noch vorliegen. Wartungs-/Einmal-Aktion — kein Hot-Path.
/// </summary>
public class RawCourseReconstructor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RawCourseCache _cache;
    private readonly ILogger<RawCourseReconstructor> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public RawCourseReconstructor(IServiceScopeFactory scopeFactory, RawCourseCache cache, ILogger<RawCourseReconstructor> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    public record Result(bool Ok, string? Error, int Chapters, int Lines, int MissingLines, int UnparseableLines = 0);

    public async Task<Result> ReconstructAsync(string bid, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 1) Neueste getCourse-Antwort für den bid, die als Kurs MIT Kapiteln parst (Fehler-/Leer-
        //    Antworten wie BOOK_NOT_OWNED überspringen).
        var courseRows = await db.ChessableRawResponses.AsNoTracking()
            .Where(r => r.Endpoint == "course" && r.Url.Contains("bid=" + bid))
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new { r.Url, r.RawJson }).Take(20).ToListAsync(ct);

        string? courseJson = null;
        ResponseCourse? course = null;
        foreach (var row in courseRows)
        {
            if (!UrlHasParam(row.Url, "bid", bid)) continue;
            var body = SafeDecompress(row.RawJson);
            if (body is null) continue;
            var parsed = SafeParse<ResponseCourse>(body);
            if (parsed?.Course?.Data is { Count: > 0 }) { courseJson = body; course = parsed; break; }
        }
        if (course is null || courseJson is null)
            return new Result(false, "Keine verwertbare getCourse-Antwort im Audit-Log (evtl. Retention abgelaufen oder nie besessen).", 0, 0, 0);

        var rest = new RestResponseCourse { CourseJsonContent = courseJson };
        int totalLines = 0, missing = 0, unparseable = 0;
        var deadOids = new List<int>();   // Oids ohne verwertbaren Inhalt → als Lücke behandeln

        foreach (var chapter in course.Course.Data)
        {
            // 2) Kapitel-Struktur (getList) aus dem Audit.
            var chapRows = await db.ChessableRawResponses.AsNoTracking()
                .Where(r => r.Endpoint == "chapter" && r.Url.Contains("bid=" + bid) && r.Url.Contains("lid=" + chapter.Id))
                .OrderByDescending(r => r.RequestedAt)
                .Select(r => new { r.Url, r.RawJson }).Take(10).ToListAsync(ct);

            string chapterJson = "";
            ResponseChapter? respChapter = null;
            foreach (var cr in chapRows)
            {
                if (!UrlHasParam(cr.Url, "lid", chapter.Id.ToString())) continue;
                var body = SafeDecompress(cr.RawJson);
                if (body is null) continue;
                var parsed = SafeParse<ResponseChapter>(body);
                if (parsed is not null) { chapterJson = body; respChapter = parsed; break; }
            }

            var restChapter = new RestResponseChapter { ChapterJsonContent = chapterJson };
            foreach (var oid in respChapter?.List.Data.Select(l => l.Id) ?? Enumerable.Empty<int>())
            {
                totalLines++;
                var content = await GetLineContentAsync(db, oid, ct);
                if (string.IsNullOrEmpty(content))
                {
                    missing++;
                    content = "";           // tote/entfernte Linie → als Lücke (IsComplete toleriert bis maxUnusableLines)
                    deadOids.Add(oid);
                }
                else if (SafeParse<ResponseLine>(content) is null)
                {
                    // Nicht-leerer, aber unparsbarer Inhalt (Proxy-Cut/abgeschnitten). Im NORMALEN Betrieb
                    // lehnt IsComplete das hart ab (Kurs neu holen). Bei der Einmal-Rekonstruktion ist ein
                    // frisches Holen nicht möglich (Kurs nicht besessen) → wie eine tote Linie behandeln
                    // (leeren → als tolerierbare Lücke) und separat zählen, statt die ganze Rekonstruktion zu kippen.
                    unparseable++;
                    content = "";
                    deadOids.Add(oid);
                }
                restChapter.ResponseLineList.Add(new RestResponseLine { Oid = oid, LineJsonContent = content });
            }
            rest.ChapterList.Add(restChapter);
        }

        // 3) Tote/abgeschnittene Linien im permanenten Linien-Cache neutralisieren: der Lesepfad
        //    (RawCourseCache.GetAsync → ReconstructLinesAsync) füllt leere Linien aus CachedRawLines nach
        //    und IsComplete lehnt einen nicht-leeren, unparsbaren Inhalt HART ab. Bliebe die abgeschnittene
        //    Zeile stehen, würde der frisch geschriebene Cache beim ersten Lesen sofort wieder verworfen.
        //    Also die betroffenen Oid-Zeilen entfernen → beim Lesen echte (tolerierbare) Lücke statt Gift.
        if (deadOids.Count > 0)
        {
            foreach (var chunk in deadOids.Distinct().Chunk(500))
            {
                var rows = await db.CachedRawLines.Where(c => chunk.Contains(c.Oid)).ToListAsync(ct);
                foreach (var row in rows)
                {
                    // Der Delete ist irreversibel (bei unowned Kursen kein Re-Fetch, line-Audit hat
                    // Retention) → die Bytes vor dem Entfernen als Forensik-Snippet nach ES loggen.
                    var snippet = SafeDecompress(row.LineJsonContent ?? "") ?? "<nicht dekomprimierbar>";
                    _logger.LogWarning(
                        "Reconstruct bid {Bid}: lösche unbrauchbare CachedRawLine oid {Oid} ({Length} Zeichen), Snippet: {Snippet}",
                        bid, row.Oid, snippet.Length, snippet.Length > 300 ? snippet[..300] + "…" : snippet);
                }
                if (rows.Count > 0) db.CachedRawLines.RemoveRange(rows);
            }
            await db.SaveChangesAsync(ct);
        }

        // 4) In den servable Cache legen. Toleranz = DIESELBE Instanz-Toleranz wie im Lese-/Schreibpfad
        //    (RawCourseCache.MaxUnusableLines), damit ein hier als vollständig eingestufter Kurs nicht
        //    von SetAsync verweigert bzw. beim ersten Lesen als „zu viele Lücken" verworfen wird.
        int dead = missing + unparseable;
        if (!RawCourseCache.IsComplete(rest, _cache.MaxUnusableLines))
            return new Result(false,
                $"Rekonstruktion unvollständig — {dead}/{totalLines} Linien unbrauchbar ({missing} leer, {unparseable} abgeschnitten), mehr als toleriert; Cache NICHT geschrieben.",
                course.Course.Data.Count, totalLines, missing, unparseable);

        await _cache.SetAsync(bid, rest, ct);
        _logger.LogInformation("RawCourse aus Rohdaten rekonstruiert: bid {Bid}, {Chapters} Kapitel, {Lines} Linien ({Missing} leer, {Unparseable} abgeschnitten)",
            bid, course.Course.Data.Count, totalLines, missing, unparseable);
        return new Result(true, null, course.Course.Data.Count, totalLines, missing, unparseable);
    }

    /// <summary>Linien-Inhalt zuerst aus dem permanenten Linien-Cache, sonst aus dem <c>line</c>-Audit.</summary>
    private async Task<string?> GetLineContentAsync(AppDbContext db, int oid, CancellationToken ct)
    {
        var cached = await db.CachedRawLines.AsNoTracking().FirstOrDefaultAsync(c => c.Oid == oid, ct);
        if (cached is not null && !string.IsNullOrEmpty(cached.LineJsonContent))
        {
            var body = SafeDecompress(cached.LineJsonContent);
            if (!string.IsNullOrWhiteSpace(body) && body != "{}") return body;
        }
        var rows = await db.ChessableRawResponses.AsNoTracking()
            .Where(r => r.Endpoint == "line" && r.Url.Contains("oid=" + oid))
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new { r.Url, r.RawJson }).Take(5).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (!UrlHasParam(row.Url, "oid", oid.ToString())) continue;
            var body = SafeDecompress(row.RawJson);
            if (!string.IsNullOrWhiteSpace(body) && body != "{}") return body;
        }
        return null;
    }

    /// <summary>Exakter Query-Parameter-Vergleich (verhindert, dass <c>bid=5193</c> auf <c>bid=51930</c> matcht).</summary>
    internal static bool UrlHasParam(string url, string key, string val)
    {
        var m = Regex.Match(url, $@"[?&]{Regex.Escape(key)}=([^&]+)");
        return m.Success && m.Groups[1].Value == val;
    }

    private static string? SafeDecompress(string gz) { try { return GzipText.Decompress(gz); } catch { return null; } }
    private static T? SafeParse<T>(string json) { try { return JsonSerializer.Deserialize<T>(json, JsonOpts); } catch { return default; } }
}
