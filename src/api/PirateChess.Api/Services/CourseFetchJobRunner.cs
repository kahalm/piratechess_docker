using Serilog.Context;
using PirateChess.Api.Controllers;
using piratechess_lib;

namespace PirateChess.Api.Services;

/// <summary>
/// Führt einen course/start-Job im Hintergrund aus (Zerlegung Z47, vorher im <see cref="ChessableDirectController"/>):
/// Kursdaten über <see cref="CourseDataProvider"/> (Cache → Bid-Lock → Abruf), Fortschritt in den
/// <see cref="CourseFetchJob"/>, PGN über die Bibliothek. Abbruch per DELETE course/{jobId} oder beim Container-Stopp.
/// Loggt unter der Kategorie des Controllers, damit Log-Filter gleich bleiben.
/// </summary>
public class CourseFetchJobRunner
{
    private readonly CourseFetchJobStore _jobStore;
    private readonly CourseDataProvider _courseData;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ChessableDirectController> _logger;

    public CourseFetchJobRunner(CourseFetchJobStore jobStore, CourseDataProvider courseData,
        IHostApplicationLifetime lifetime, ILogger<ChessableDirectController> logger)
    {
        _jobStore = jobStore;
        _courseData = courseData;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Legt den Job an und startet ihn (fire-and-forget); liefert die JobId.</summary>
    /// <param name="preloaded">Im Start-Gate schon geladene Rohdaten (nur ohne Bearer und ohne Force-Refresh).</param>
    public string Start(string bearer, string uid, string bid, TrainingMode mode, bool forceRefresh, RestResponseCourse? preloaded)
    {
        var jobId = Guid.NewGuid().ToString("N");
        _jobStore.Create(jobId);
        // Fire-and-forget: Runner + _jobStore sind Singletons → nach Controller-Dispose gültig.
        // Abbruch über DELETE course/{jobId} oder beim Container-Stopp (siehe RunAsync).
        _ = Task.Run(() => RunAsync(jobId, bearer, uid, bid, mode, forceRefresh, preloaded));
        return jobId;
    }

    private async Task RunAsync(string jobId, string bearer, string uid, string bid, TrainingMode mode, bool forceRefresh,
        RestResponseCourse? preloaded)
    {
        var job = _jobStore.Get(jobId);
        if (job is null) return;
        // Abbruch (S2-008): DELETE course/{jobId} oder Container-Stopp. Das Token reicht bis zum curl-Kill.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.CancellationToken, _lifetime.ApplicationStopping);
        var ct = cts.Token;
        // Lifecycle-Logs dieses Fetch-Jobs für die zentrale Kibana-Filterung taggen → ECS `tags`.
        using var _tagScope = LogContext.PushProperty("LogTags", "chessable,scrape");
        try
        {
            var fetch = await FetchWithProgressAsync(job, bid, bearer, uid, forceRefresh, preloaded, ct);
            if (fetch.BearerMissing)
            {
                // Der Start ohne Bearer wurde nur zugelassen, weil der Kurs gecacht war (StartCourse-Gate reicht
                // die Daten mit). Ohne Daten liefe der Chessable-Fetch mit leerem Bearer in ein irreführendes
                // „Invalid bearer" — stattdessen den echten Grund melden.
                job.Fail("Kurs nicht (mehr) im Rohdaten-Cache und kein Bearer übergeben — Start mit Bearer wiederholen.");
                return;
            }
            if (fetch.FetchError is not null)
            {
                job.Fail(fetch.FetchError);
                return;
            }
            await CompleteAsync(job, jobId, bid, mode, fetch.Data, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Cancel();
            _logger.LogInformation("Kurs-Fetch-Job {JobId} bid {Bid} abgebrochen", jobId, bid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Course fetch job {JobId} failed for bid {Bid}", jobId, bid);
            job.Fail(ex.Message);
        }
    }

    /// <summary>Kursdaten holen und dabei die Kapitel-/Linienzähler des Jobs nachführen.</summary>
    private Task<CourseDataResult> FetchWithProgressAsync(CourseFetchJob job, string bid, string bearer, string uid,
        bool forceRefresh, RestResponseCourse? preloaded, CancellationToken ct)
        => _courseData.GetOrFetchAsync(bid, bearer, uid, forceRefresh, preloaded,
            onChapterProgress: counter =>
            {
                var parts = counter.Split('/');
                if (parts.Length == 2)
                {
                    if (int.TryParse(parts[0].Trim(), out var d)) job.ChaptersDone = d;
                    if (int.TryParse(parts[1].Trim(), out var t)) job.ChaptersTotal = t;
                }
            },
            onCumulativeLines: total =>
            {
                if (int.TryParse(total.Trim(), out var l)) job.LinesDone = l;
            },
            onTotalLines: t => job.LinesTotal = t,
            ct: ct);

    /// <summary>Zähler auf „fertig" setzen, PGN erzeugen und den Job abschließen.</summary>
    private async Task CompleteAsync(CourseFetchJob job, string jobId, string bid, TrainingMode mode,
        RestResponseCourse? data, CancellationToken ct)
    {
        if (data is not null)
        {
            job.ChaptersTotal = data.ChapterList.Count;
            job.ChaptersDone = data.ChapterList.Count;
            job.LinesDone = data.ChapterList.Sum(c => c.ResponseLineList.Count);
            job.LinesTotal = job.LinesDone; // gecacht/fertig → vollständige Zahl
        }

        var lib = CoursePgn.CreateLib(data, mode, detail =>
            _logger.LogWarning("Chessable-Parser " + LogWatcherContract.ParserSkipped + " (job {JobId}, bid {Bid}): {Detail}", jobId, bid, detail));
        ct.ThrowIfCancellationRequested();
        var (pgn, courseName) = await Task.Run(() => lib.GetCourse(bid, useLocalData: true), ct);
        if (lib.ErrorCount > 0)
            _logger.LogWarning("Kurs-Fetch-Job {JobId} bid {Bid} mit {Errors} übersprungenen Linien/Kapiteln abgeschlossen", jobId, bid, lib.ErrorCount);
        var lnCount = CoursePgn.LineCount(data);
        if (lnCount > job.LinesTotal) job.LinesTotal = lnCount; // tatsächliche Zahl ist autoritativ
        job.Complete(pgn, courseName, CoursePgn.ChapterCount(data), lnCount);
    }
}
