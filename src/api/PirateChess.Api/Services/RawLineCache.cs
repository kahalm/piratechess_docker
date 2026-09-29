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
/// Roh-Content kann groß sein (einzelne Linien &gt;500 KB) → gzip+Base64, wie der Kurs-Cache.
///
/// <para><b>Ungültige Linien werden markiert, nie gelöscht</b> (<see cref="CachedRawLine.InvalidAt"/>). Anlass:
/// oid 36114125 (Kurs 207313) lag seit 2026-06-14 abgeschnitten im Cache; die Extension hielt die Linie für
/// gecacht, schickte nur ihre oid, und der Parser übersprang sie bei jedem Import still. Geprüft wird mit der
/// gemeinsamen Regel <see cref="ChessableContent.LineReason"/> (<see cref="InvalidReason"/>) — beim Schreiben, beim
/// Auffüllen eines Imports und beim Resume-Lesen. Markierte Linien gelten nirgends als gecacht; ihr Inhalt bleibt, und
/// <see cref="RevalidateAsync"/> gibt sie wieder frei, wenn sie eine korrigierte Prüfung bestehen. Ersetzt wird
/// eine markierte Linie nur durch eine gültige aus einem eigenen Server-Abruf, der alte Inhalt wandert dabei nach
/// <see cref="CachedRawLineArchive"/>; Browser-Inhalt ersetzt sie nie.</para>
/// <para><b>Jede Linie gehört zu einem Kurs</b> (<see cref="CachedRawLine.Bid"/>): gefüllt wird nur für denselben bid.
/// Altzeilen ohne bid füllen weiter jeden Kurs.</para>
/// </summary>
public class RawLineCache
{
    private const int InvalidReasonMaxLength = 200;
    private const int MaxListed = 1000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RawLineCache> _logger;

    public RawLineCache(IServiceScopeFactory scopeFactory, ILogger<RawLineCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Eine Linie ist nicht leer, wenn ihr Roh-Content nicht leer und nicht <c>{}</c> ist.</summary>
    public static bool IsComplete(string? content) => !ChessableContent.IsEmpty(content);

    /// <summary>
    /// Warum ein Linien-Inhalt nicht taugt, oder <c>null</c> — die gemeinsame Regel
    /// <see cref="ChessableContent.LineReason"/> (Parser-Deserialisierung, game-Objekt, Chessable-Fehlerkörper).
    /// </summary>
    public static string? InvalidReason(string? content) => ChessableContent.LineReason(content);

    private static string Trim(string s) => s.Length <= InvalidReasonMaxLength ? s : s[..InvalidReasonMaxLength];

    /// <summary>Darf die Zeile einen Import dieses Kurses füllen? Ohne bid der Anfrage oder der Zeile (Altbestand): ja.</summary>
    private static bool SameCourse(string? rowBid, string? bid) => bid is null || rowBid is null || rowBid == bid;

    /// <summary>Nur Zeilen, die einen Import dieses Kurses füllen dürfen (siehe <see cref="SameCourse"/>).</summary>
    private static IQueryable<CachedRawLine> OfCourse(IQueryable<CachedRawLine> rows, string? bid)
        => bid is null ? rows : rows.Where(c => c.Bid == null || c.Bid == bid);

    /// <summary>Ein eigener Server-Abruf hat den Inhalt geliefert: Kurs übernehmen, Browser-Herkunft zurücksetzen.</summary>
    private static void FromServer(CachedRawLine row, string? bid)
    {
        if (bid is not null) row.Bid = bid;
        row.FromBrowser = false;
    }

    /// <summary>Gespeicherten Inhalt entpacken und prüfen. Ein Entpackfehler ist ebenfalls ein Grund.</summary>
    private static (string? Content, string? Reason) Unpack(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return (null, "leer");
        string content;
        try
        {
            content = GzipText.Decompress(stored);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, Trim("gzip/Base64: " + ex.Message));
        }
        return (content, InvalidReason(content));
    }

    private static void MarkInvalid(CachedRawLine row, string reason, DateTime now)
    {
        row.InvalidAt = now;
        row.InvalidReason = reason;
    }

    private static void Archive(AppDbContext db, CachedRawLine row, DateTime now)
        => db.CachedRawLineArchive.Add(new CachedRawLineArchive
        {
            Oid = row.Oid,
            LineJsonContent = row.LineJsonContent,
            CachedAt = row.CachedAt,
            InvalidAt = row.InvalidAt,
            InvalidReason = row.InvalidReason,
            ArchivedAt = now,
        });

    /// <summary>Eine markierte Zeile durch gültigen Inhalt ersetzen; der alte Inhalt kommt ins Archiv.</summary>
    internal static void Heal(AppDbContext db, CachedRawLine row, string compressed, DateTime now)
    {
        Archive(db, row, now);
        row.LineJsonContent = compressed;
        row.CachedAt = now;
        row.InvalidAt = null;
        row.InvalidReason = null;
    }

    /// <summary>
    /// Liefert den gecachten Roh-Content der Linie (oid) oder null, wenn nicht vorhanden, ungültig oder unter einem
    /// anderen Kurs als <paramref name="bid"/> abgelegt. Stellt sich eine bisher unmarkierte Zeile als ungültig
    /// heraus, wird sie markiert — der Server-Abruf holt sie dann neu.
    /// </summary>
    public async Task<string?> GetAsync(int oid, string? bid = null, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CachedRawLines.FirstOrDefaultAsync(c => c.Oid == oid, ct);
            if (row is null || row.InvalidAt is not null || string.IsNullOrEmpty(row.LineJsonContent)
                || !SameCourse(row.Bid, bid)) return null;
            var (content, reason) = Unpack(row.LineJsonContent);
            if (reason is null) return content;
            MarkInvalid(row, reason, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Linien-Cache: oid {Oid} ist ungültig ({Reason}) — markiert, Inhalt bleibt erhalten", oid, reason);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawLineCache.Get fehlgeschlagen für oid {Oid}", oid);
            return null;
        }
    }

    /// <summary>
    /// Legt eine Linie aus einem eigenen Server-Abruf ab (Upsert, unter <paramref name="bid"/>). Leere/<c>{}</c>-Antworten
    /// werden ignoriert. Eine ungültige Antwort wird nur aufgehoben, wenn es für die oid noch nichts gibt (markiert) —
    /// einen vorhandenen Stand ersetzt sie nie.
    /// </summary>
    public async Task SetAsync(int oid, string content, string? bid = null, CancellationToken ct = default)
    {
        if (!IsComplete(content))
            return;
        try
        {
            var reason = InvalidReason(content);
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var compressed = GzipText.Compress(content);
            var now = DateTime.UtcNow;
            var row = await db.CachedRawLines.FirstOrDefaultAsync(c => c.Oid == oid, ct);
            if (row is null)
            {
                db.CachedRawLines.Add(new CachedRawLine
                {
                    Oid = oid, LineJsonContent = compressed, CachedAt = now, Bid = bid,
                    InvalidAt = reason is null ? null : now, InvalidReason = reason,
                });
                if (reason is not null)
                    _logger.LogWarning("Linien-Cache: ungültige Linie {Oid} ({Reason}) nur markiert abgelegt", oid, reason);
            }
            else if (reason is not null)
            {
                _logger.LogWarning("Linien-Cache: ungültige Antwort für oid {Oid} ({Reason}) verworfen — vorhandener Stand bleibt", oid, reason);
                return;
            }
            else if (row.InvalidAt is not null)
            {
                Heal(db, row, compressed, now);
                FromServer(row, bid);
            }
            else
            {
                row.LineJsonContent = compressed;
                row.CachedAt = now;
                FromServer(row, bid);
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawLineCache.Set fehlgeschlagen für oid {Oid}", oid);
        }
    }

    /// <summary>
    /// Welche der oids liegen gültig im Cache — nur die Existenz, kein Inhalt (gebatcht). Markierte fehlen, mit
    /// <paramref name="bid"/> auch Linien eines anderen Kurses.
    /// </summary>
    public async Task<HashSet<int>> GetCachedOidsAsync(IEnumerable<int> oids, string? bid = null, CancellationToken ct = default)
    {
        var result = new HashSet<int>();
        var wanted = oids.Where(o => o > 0).Distinct().ToList();
        if (wanted.Count == 0) return result;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var chunk in wanted.Chunk(1000))
                result.UnionWith(await OfCourse(db.CachedRawLines.AsNoTracking(), bid)
                    .Where(c => chunk.Contains(c.Oid) && c.InvalidAt == null && c.LineJsonContent != null && c.LineJsonContent != "")
                    .Select(c => c.Oid)
                    .ToListAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RawLineCache.GetCachedOids fehlgeschlagen für {Count} oids", wanted.Count);
        }
        return result;
    }

    /// <summary>
    /// Inhalte mehrerer Linien auf einmal (gebatcht, dekomprimiert, geprüft). Nicht gecachte, ungültige und — mit
    /// <paramref name="bid"/> — unter einem anderen Kurs abgelegte fehlen im Ergebnis; eine bisher unmarkierte ungültige
    /// Zeile wird dabei markiert, damit der nächste Import sie neu holt.
    /// </summary>
    public async Task<Dictionary<int, string>> GetManyAsync(IEnumerable<int> oids, string? bid = null, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        var wanted = oids.Where(o => o > 0).Distinct().ToList();
        if (wanted.Count == 0) return result;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var chunk in wanted.Chunk(200))
            {
                var rows = await OfCourse(db.CachedRawLines.AsNoTracking(), bid)
                    .Where(c => chunk.Contains(c.Oid) && c.InvalidAt == null)
                    .Select(c => new { c.Id, c.Oid, c.LineJsonContent })
                    .ToListAsync(ct);
                var broken = new Dictionary<int, string>();   // Id → Grund
                foreach (var row in rows)
                {
                    if (string.IsNullOrEmpty(row.LineJsonContent)) continue;
                    var (content, reason) = Unpack(row.LineJsonContent);
                    if (reason is null) result[row.Oid] = content!;
                    else broken[row.Id] = reason;
                }
                if (broken.Count == 0) continue;

                var now = DateTime.UtcNow;
                var ids = broken.Keys.ToList();
                foreach (var row in await db.CachedRawLines.Where(c => ids.Contains(c.Id)).ToListAsync(ct))
                {
                    MarkInvalid(row, broken[row.Id], now);
                    _logger.LogWarning("Linien-Cache: oid {Oid} ist ungültig ({Reason}) — markiert, Inhalt bleibt erhalten", row.Oid, broken[row.Id]);
                }
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RawLineCache.GetMany fehlgeschlagen für {Count} oids", wanted.Count);
        }
        return result;
    }

    /// <summary>
    /// Legt Browser-Linien ab, für deren oid es noch GAR KEINE Zeile gibt, unter <paramref name="bid"/> und als
    /// <see cref="CachedRawLine.FromBrowser"/>, und liefert deren Anzahl. Eine vorhandene Zeile bleibt unberührt: ein
    /// gültiger Eintrag — meist vom Server selbst geholt — bleibt maßgeblich (ersetzen kann ihn nur der Force-Refresh
    /// des Server-Abrufs), und eine als ungültig markierte ersetzt nur ein eigener Server-Abruf, nie der Inhalt eines
    /// Clients. Ungültige Inhalte werden übergangen.
    /// </summary>
    public async Task<int> AddMissingAsync(IReadOnlyDictionary<int, string> lines, string? bid, CancellationToken ct = default)
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
                    .Where(c => chunkOids.Contains(c.Oid))
                    .Select(c => c.Oid)
                    .ToListAsync(ct)).ToHashSet();
                var now = DateTime.UtcNow;
                var changed = 0;
                foreach (var (oid, content) in chunk)
                {
                    if (existing.Contains(oid)) continue;
                    db.CachedRawLines.Add(new CachedRawLine
                    {
                        Oid = oid, LineJsonContent = GzipText.Compress(content), CachedAt = now, Bid = bid, FromBrowser = true,
                    });
                    changed++;
                }
                if (changed == 0) continue;
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                added += changed;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // z. B. derselbe Kurs parallel von zwei Browsern — der Cache ist nie fatal.
            _logger.LogWarning(ex, "RawLineCache.AddMissing fehlgeschlagen nach {Added} von {Count} Linien", added, candidates.Count);
        }
        return added;
    }

    public sealed record RevalidationResult(
        int Checked,
        int NewlyInvalidCount,
        IReadOnlyList<(int Oid, string Reason)> NewlyInvalid,
        int ClearedCount,
        IReadOnlyList<int> Cleared,
        int StillInvalid);

    /// <summary>
    /// Prüft Linien erneut. <paramref name="all"/>=false: nur die markierten — etwa nach einer Korrektur der Prüfung
    /// oder des Parsers, um sie wieder freizugeben; true: den ganzen Cache. <paramref name="apply"/>=false: nur
    /// Bericht. Markiert neu ungültige, gibt inzwischen gültige frei, löscht nie etwas. Listen auf 1000 gekappt,
    /// die Zähler sind vollständig.
    /// </summary>
    public async Task<RevalidationResult> RevalidateAsync(bool all, bool apply, CancellationToken ct = default)
    {
        var newlyInvalid = new List<(int, string)>();
        var cleared = new List<int>();
        int checkedCount = 0, newlyInvalidCount = 0, clearedCount = 0, stillInvalid = 0, lastId = 0;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        while (true)
        {
            var query = db.CachedRawLines.Where(c => c.Id > lastId);
            if (!all) query = query.Where(c => c.InvalidAt != null);
            var rows = await query.OrderBy(c => c.Id).Take(200).ToListAsync(ct);
            if (rows.Count == 0) break;
            var now = DateTime.UtcNow;
            foreach (var row in rows)
            {
                lastId = row.Id;
                checkedCount++;
                var reason = Unpack(row.LineJsonContent).Reason;
                if (reason is not null && row.InvalidAt is null)
                {
                    newlyInvalidCount++;
                    if (newlyInvalid.Count < MaxListed) newlyInvalid.Add((row.Oid, reason));
                    if (apply) MarkInvalid(row, reason, now);
                }
                else if (reason is null && row.InvalidAt is not null)
                {
                    clearedCount++;
                    if (cleared.Count < MaxListed) cleared.Add(row.Oid);
                    if (apply) { row.InvalidAt = null; row.InvalidReason = null; }
                }
                else if (reason is not null)
                {
                    stillInvalid++;
                }
            }
            if (apply) await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        if (apply && (newlyInvalidCount > 0 || clearedCount > 0))
            _logger.LogInformation("Linien-Cache geprüft: {Checked} Linien, {Invalid} neu markiert, {Cleared} freigegeben", checkedCount, newlyInvalidCount, clearedCount);
        return new RevalidationResult(checkedCount, newlyInvalidCount, newlyInvalid, clearedCount, cleared, stillInvalid);
    }
}
