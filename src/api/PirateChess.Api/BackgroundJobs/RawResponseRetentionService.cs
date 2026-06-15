using Microsoft.EntityFrameworkCore;
using PirateChess.Api.Data;

namespace PirateChess.Api.BackgroundJobs;

/// <summary>
/// Hält die reine Audit/Debug-Tabelle <c>ChessableRawResponses</c> klein: löscht periodisch
/// Einträge, die älter als das Retention-Fenster sind. Die Tabelle wird nirgends gelesen
/// (append-only) und wuchs sonst unbegrenzt (zeitweise &gt;10 GB durch wiederholte Re-Fetches).
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
    /// </summary>
    public static async Task<int> PruneOlderThanAsync(
        AppDbContext db, DateTime cutoff, int batchSize, CancellationToken ct = default)
    {
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.ChessableRawResponses
                .Where(r => r.RequestedAt < cutoff)
                .OrderBy(r => r.Id)
                .Take(batchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            db.ChessableRawResponses.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            total += batch.Count;
        }
        return total;
    }
}
