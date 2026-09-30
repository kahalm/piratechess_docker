using Microsoft.EntityFrameworkCore;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;

namespace PirateChess.Api.BackgroundJobs;

/// <summary>
/// Hält die Audit-Tabelle <c>ChessableRawResponses</c> klein: löscht periodisch Einträge, die älter
/// als das Retention-Fenster sind. Geschrieben wird sie append-only (<see cref="Services.RawResponseAudit"/>),
/// ohne Retention wuchs sie unbegrenzt (zeitweise &gt;10 GB durch wiederholte Re-Fetches).
/// <para><b>Kopplung an die Kurs-Rekonstruktion:</b> Die Tabelle ist nicht nur Debug-Trail.
/// <see cref="Services.RawCourseReconstructor"/> liest daraus die getCourse- und Kapitel-Antworten
/// (<c>course</c>/<c>chapter</c>) als EINZIGE Quelle der Kurs- und Kapitelstruktur (Linien-Fallback:
/// <c>line</c>). Das Fenster bestimmt also, wie lange ein Kurs ohne Besitz (BOOK_NOT_OWNED) noch
/// rekonstruierbar ist. Wer die Retention verkürzt, verkürzt diese Frist.</para>
///
/// Fenster konfigurierbar über <c>ChessableRawResponses:RetentionDays</c> (Default 14).
/// </summary>
public class RawResponseRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RawResponseRetentionService> _logger;
    private readonly TimeSpan _retention;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const int BatchSize = 2000;

    public RawResponseRetentionService(
        IServiceScopeFactory scopeFactory,
        ILogger<RawResponseRetentionService> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var days = config.GetValue<int?>("ChessableRawResponses:RetentionDays") ?? 14;
        _retention = TimeSpan.FromDays(days > 0 ? days : 14);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cutoff = DateTime.UtcNow - _retention;
                var deleted = await PruneOlderThanAsync(db, cutoff, BatchSize, stoppingToken);
                if (deleted > 0)
                    _logger.LogInformation(
                        "RawResponse-Retention: {Count} ChessableRawResponses älter als {Days} Tage gelöscht",
                        deleted, (int)_retention.TotalDays);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RawResponse-Retention-Lauf fehlgeschlagen");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Löscht alle <c>ChessableRawResponses</c> mit <c>RequestedAt &lt; cutoff</c> in Batches
    /// (schont Lock-/Transaktionsgröße). Liefert die Gesamtzahl gelöschter Zeilen.
    /// <para>Lädt nur die Ids, nie die Zeilen selbst: <c>RawJson</c> ist je Zeile bis zu ~170 KB Base64,
    /// ganze Entities hätten je Batch Hunderte MB in den Heap geholt. Gelöscht wird über Stub-Entities
    /// (nur Id) — dieselben DELETE-Anweisungen wie zuvor mit geladenen Entities, auch unter InMemory.</para>
    /// </summary>
    public static async Task<int> PruneOlderThanAsync(
        AppDbContext db, DateTime cutoff, int batchSize, CancellationToken ct = default)
    {
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            var ids = await db.ChessableRawResponses
                .Where(r => r.RequestedAt < cutoff)
                .OrderBy(r => r.Id)
                .Select(r => r.Id)
                .Take(batchSize)
                .ToListAsync(ct);
            if (ids.Count == 0) break;
            var local = db.ChessableRawResponses.Local;   // einmal je Batch (der Zugriff löst DetectChanges aus)
            foreach (var id in ids)
            {
                // Schon getrackte Zeile (gleicher Kontext) wiederverwenden, sonst kollidiert der Stub mit ihr.
                var row = local.FindEntry(id)?.Entity ?? new ChessableRawResponse { Id = id };
                db.ChessableRawResponses.Remove(row);
            }
            await db.SaveChangesAsync(ct);
            total += ids.Count;
        }
        return total;
    }
}
