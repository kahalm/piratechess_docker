using Microsoft.AspNetCore.Mvc;
using Serilog.Context;
using PirateChess.Api.Authorization;
using PirateChess.Api.Models.DTOs;
using PirateChess.Api.Services;

namespace PirateChess.Api.Controllers;

/// <summary>
/// Stateless Chessable endpoints for service-to-service callers (rookhub).
/// The bearer is passed per request and never persisted in piratechess.
/// Authenticated via the <c>X-Service-Key</c> header (see <see cref="ServiceKeyAuthAttribute"/>).
/// </summary>
[ApiController]
[Route("api/chessable/direct")]
[ServiceKeyAuth]
public class ChessableDirectController : ControllerBase
{
    private readonly IChessableHttpService _chessableHttp;
    private readonly CourseFetchJobStore _jobStore;
    private readonly RawCourseCache _rawCache;
    private readonly VpnIpHealth _ipHealth;
    private readonly ILogger<ChessableDirectController> _logger;

    public ChessableDirectController(
        IChessableHttpService chessableHttp,
        CourseFetchJobStore jobStore,
        RawCourseCache rawCache,
        VpnIpHealth ipHealth,
        ILogger<ChessableDirectController> logger)
    {
        _chessableHttp = chessableHttp;
        _jobStore = jobStore;
        _rawCache = rawCache;
        _ipHealth = ipHealth;
        _logger = logger;
    }

    /// <summary>Per-IP-Auswertung: wie viele Requests/Blocks pro VPN-Ausgangs-IP (über alle Rotationen),
    /// schlechteste zuerst. Für „welche IP ist immer wieder schlecht".</summary>
    [HttpGet("debug/ip-health")]
    public IActionResult IpHealth() => Ok(_ipHealth.Snapshot());

    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] DirectBearerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Bearer))
            return BadRequest(new { message = "Bearer is required" });

        var (uid, uidError) = _chessableHttp.ExtractUidFromBearer(request.Bearer);
        if (uidError is not null)
            return BadRequest(new { message = uidError });

        var (courses, error) = await _chessableHttp.GetCoursesAsync(request.Bearer, uid, ct);
        if (error is not null)
        {
            var cleanMessage = error.Trim() is "{}" or "" ? "Invalid bearer" : error;
            return BadRequest(new { message = cleanMessage });
        }

        return Ok(new DirectTestResponse(uid, courses!.Count));
    }

    [HttpPost("courses")]
    public async Task<IActionResult> Courses([FromBody] DirectBearerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Bearer))
            return BadRequest(new { message = "Bearer is required" });

        var (uid, uidError) = _chessableHttp.ExtractUidFromBearer(request.Bearer);
        if (uidError is not null)
            return BadRequest(new { message = uidError });

        var (courses, error) = await _chessableHttp.GetCoursesAsync(request.Bearer, uid, ct);
        if (error is not null)
        {
            var cleanMessage = error.Trim() is "{}" or "" ? "Invalid bearer" : error;
            return BadRequest(new { message = cleanMessage });
        }

        var result = courses!.Select(c => new CourseListItem(c.Key, c.Value)).ToList();
        return Ok(result);
    }

    /// <summary>
    /// Tiefer Kurs-Abruf: holt die komplette Kursstruktur (Kapitel/Linien/PGN) und gibt sie als
    /// ein PGN zurück, dessen Trainingsannotation per <c>Mode</c> gesteuert wird. rookhub nutzt
    /// das, um denselben Kurs als Repertoire (<c>None</c>) oder als Buch (<c>FirstKeyMove</c>,
    /// erster Key-Zug trainierbar) zu importieren. Bearer wird nicht persistiert.
    /// </summary>
    [HttpPost("course")]
    public async Task<IActionResult> Course([FromBody] DirectCourseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Bearer))
            return BadRequest(new { message = "Bearer is required" });
        if (string.IsNullOrWhiteSpace(request.Bid))
            return BadRequest(new { message = "Bid is required" });

        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "FirstKeyMove" : request.Mode;
        string[] validModes = ["AllKeyMoves", "FirstKeyMove", "None"];
        if (!validModes.Contains(mode))
            return BadRequest(new { message = "Invalid mode. Use: AllKeyMoves, FirstKeyMove, None" });

        var (uid, uidError) = _chessableHttp.ExtractUidFromBearer(request.Bearer);
        if (uidError is not null)
            return BadRequest(new { message = uidError });

        var data = await _rawCache.GetAsync(request.Bid, ct);
        if (data is null)
        {
            // Per-Bid-Lock: kein doppelter Chessable-Abruf desselben Kurses über die VPN-IP.
            var gate = _rawCache.BidLock(request.Bid);
            await gate.WaitAsync(ct);
            try
            {
                data = await _rawCache.GetAsync(request.Bid, ct); // Double-Check: paralleler Fetch evtl. fertig
                if (data is null)
                {
                    var (fetched, fetchError) = await _chessableHttp.FetchCourseDataAsync(request.Bearer, uid, request.Bid, ct: ct);
                    if (fetchError is not null)
                    {
                        var cleanMessage = fetchError.Trim() is "{}" or "" ? "Invalid bearer" : fetchError;
                        _logger.LogWarning("Course fetch failed for bid {Bid} (uid {Uid}): {Error}", request.Bid, uid, cleanMessage);
                        return BadRequest(new { message = cleanMessage });
                    }
                    data = fetched;
                    if (data is not null) await _rawCache.SetAsync(request.Bid, data, ct);
                }
            }
            finally { gate.Release(); }
        }

        var lib = new piratechess_lib.PirateChessLib { restResponseCourse = data };
        switch (mode)
        {
            case "AllKeyMoves": lib.AllKeyMovesTraining = true; lib.NoTrainingMove = false; break;
            case "FirstKeyMove": lib.AllKeyMovesTraining = false; lib.NoTrainingMove = false; break;
            case "None": lib.AllKeyMovesTraining = false; lib.NoTrainingMove = true; break;
        }

        string pgn, courseName;
        try
        {
            (pgn, courseName) = await Task.Run(() => lib.GetCourse(request.Bid, useLocalData: true), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PGN generation failed for bid {Bid} (uid {Uid})", request.Bid, uid);
            return BadRequest(new { message = $"PGN generation failed: {ex.Message}" });
        }

        var chapterCount = data?.ChapterList.Count ?? 0;
        var lineCount = data?.ChapterList.Sum(c => c.ResponseLineList.Count) ?? 0;

        return Ok(new DirectCourseResponse(request.Bid, courseName, mode, chapterCount, lineCount, pgn));
    }

    /// <summary>
    /// Startet den tiefen Kurs-Abruf asynchron und liefert eine JobId. Der Fortschritt
    /// (Kapitel/Linien) ist über <c>GET /api/chessable/direct/course/{jobId}</c> abrufbar; dort
    /// kommt bei Status "completed" auch das fertige PGN. Für Fortschrittsanzeige in rookhub.
    /// </summary>
    /// <summary>Ob die Rohdaten dieses Kurses schon gecacht sind (→ Import braucht keinen Chessable-Abruf).</summary>
    [HttpGet("course/{bid}/cached")]
    public async Task<IActionResult> CourseCached(string bid, CancellationToken ct)
        => Ok(new { cached = await _rawCache.ExistsAsync(bid, ct) });

    /// <summary>Alle gecachten Kurs-Bids auf einmal — rookhub reichert damit die Kursliste mit einem
    /// „gecacht/sofort verfügbar"-Flag an (1 Call statt N).</summary>
    [HttpGet("courses/cached")]
    public async Task<IActionResult> CachedBids(CancellationToken ct)
        => Ok(new { bids = (await _rawCache.GetAllCachedBidsAsync(ct)).ToList() });

    /// <summary>Leichte Vorab-Schätzung der Gesamt-Linienzahl eines Kurses (für die Admin-Kursliste).
    /// Gecacht → aus dem Rohdaten-Cache (kein Chessable-Call); sonst EIN getCourse?includeVariations.</summary>
    [HttpPost("course/info")]
    public async Task<IActionResult> CourseInfo([FromBody] DirectCourseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Bearer))
            return BadRequest(new { message = "Bearer is required" });
        if (string.IsNullOrWhiteSpace(request.Bid))
            return BadRequest(new { message = "Bid is required" });

        // Gecacht → Gesamtzahl ohne Chessable-Abruf aus den Rohdaten.
        var cached = await _rawCache.GetAsync(request.Bid, ct);
        if (cached is not null)
            return Ok(new DirectCourseInfoResponse(request.Bid, cached.ChapterList.Sum(c => c.ResponseLineList.Count), true));

        var (uid, uidError) = _chessableHttp.ExtractUidFromBearer(request.Bearer);
        if (uidError is not null)
            return BadRequest(new { message = uidError });

        var (total, error) = await _chessableHttp.GetCourseLineCountAsync(request.Bearer, uid, request.Bid, ct);
        if (error is not null)
            return BadRequest(new { message = error });
        return Ok(new DirectCourseInfoResponse(request.Bid, total ?? 0, false));
    }

    /// <summary>Diagnose: holt GENAU eine Linie (getGame für eine oid) über den echten Abruf-Pfad
    /// (curl-impersonate + VPN-Tunnel) und meldet Timing + ob die Antwort vollständig ist. Mehrfach
    /// aufrufen reproduziert ggf. das Soft-Rate-Limit/den Block bei Linien-Abrufen unter Last.</summary>
    [HttpPost("debug/line")]
    public async Task<IActionResult> DebugLine([FromBody] DirectLineDebugRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Bearer))
            return BadRequest(new { message = "Bearer is required" });
        if (request.Oid <= 0)
            return BadRequest(new { message = "Oid is required" });
        var (uid, uidError) = _chessableHttp.ExtractUidFromBearer(request.Bearer);
        if (uidError is not null)
            return BadRequest(new { message = uidError });

        var (ok, bytes, ms, error, snippet) = await _chessableHttp.DebugFetchLineAsync(request.Bearer, uid, request.Oid, ct);
        return Ok(new DirectLineDebugResponse(request.Oid, uid, ok, bytes, ms, error, snippet));
    }

    [HttpPost("course/start")]
    public IActionResult StartCourse([FromBody] DirectCourseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Bearer))
            return BadRequest(new { message = "Bearer is required" });
        if (string.IsNullOrWhiteSpace(request.Bid))
            return BadRequest(new { message = "Bid is required" });

        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "FirstKeyMove" : request.Mode;
        string[] validModes = ["AllKeyMoves", "FirstKeyMove", "None"];
        if (!validModes.Contains(mode))
            return BadRequest(new { message = "Invalid mode. Use: AllKeyMoves, FirstKeyMove, None" });

        var (uid, uidError) = _chessableHttp.ExtractUidFromBearer(request.Bearer);
        if (uidError is not null)
            return BadRequest(new { message = uidError });

        var jobId = Guid.NewGuid().ToString("N");
        _jobStore.Create(jobId);
        // Fire-and-forget: _chessableHttp + _jobStore sind Singletons → nach Controller-Dispose gültig.
        _ = Task.Run(() => RunFetchAsync(jobId, request.Bearer, uid, request.Bid, mode));
        return Ok(new DirectCourseStartResponse(jobId));
    }

    /// <summary>Fortschritt/Ergebnis eines Kurs-Abruf-Jobs. Terminaler Status liefert das PGN und räumt den Job ab.</summary>
    [HttpGet("course/{jobId}")]
    public IActionResult CourseProgress(string jobId)
    {
        var job = _jobStore.Get(jobId);
        if (job is null) return NotFound(new { message = "Job not found" });

        // Konsistenter Schnappschuss unter Lock: Status + Pgn werden zusammenhängend gelesen (kein
        // "completed, aber Pgn noch null"-Race vor dem Remove).
        var s = job.Snapshot();
        var dto = new DirectCourseProgressResponse(
            s.Status, s.ChaptersDone, s.ChaptersTotal, s.LinesDone, s.LinesTotal,
            s.ChapterCount, s.LineCount, s.CourseName,
            s.Status == "completed" ? s.Pgn : null, s.Error);

        if (s.Status is "completed" or "failed")
            _jobStore.Remove(jobId); // einmaliger Terminal-Read

        return Ok(dto);
    }

    private async Task RunFetchAsync(string jobId, string bearer, string uid, string bid, string mode)
    {
        var job = _jobStore.Get(jobId);
        if (job is null) return;
        // Lifecycle-Logs dieses Fetch-Jobs für die zentrale Kibana-Filterung taggen → ECS `tags`.
        using var _tagScope = LogContext.PushProperty("LogTags", "chessable,scrape");
        try
        {
            // Rohdaten aus dem (kurs-/bid-weiten) Cache wiederverwenden → kein Chessable-Call,
            // auch wenn ein anderer User denselben Kurs schon geholt hat.
            var data = await _rawCache.GetAsync(bid);
            if (data is null)
            {
                // Per-Bid-Lock: zwei parallele Cache-Misses desselben Kurses sollen nicht beide über
                // die VPN-IP fetchen. Nach Lock-Eintritt erneut prüfen (ein paralleler Fetch könnte den
                // Cache inzwischen gefüllt haben).
                var gate = _rawCache.BidLock(bid);
                await gate.WaitAsync();
                try
                {
                    data = await _rawCache.GetAsync(bid);
                    if (data is null)
                    {
                        var (fetched, fetchError) = await _chessableHttp.FetchCourseDataAsync(bearer, uid, bid,
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
                            onTotalLines: t => job.LinesTotal = t);

                        if (fetchError is not null)
                        {
                            job.Fail(fetchError.Trim() is "{}" or "" ? "Invalid bearer" : fetchError);
                            return;
                        }
                        data = fetched;
                        if (data is not null) await _rawCache.SetAsync(bid, data);
                    }
                }
                finally { gate.Release(); }
            }

            if (data is not null)
            {
                job.ChaptersTotal = data.ChapterList.Count;
                job.ChaptersDone = data.ChapterList.Count;
                job.LinesDone = data.ChapterList.Sum(c => c.ResponseLineList.Count);
                job.LinesTotal = job.LinesDone; // gecacht/fertig → vollständige Zahl
            }

            var lib = new piratechess_lib.PirateChessLib { restResponseCourse = data };
            switch (mode)
            {
                case "AllKeyMoves": lib.AllKeyMovesTraining = true; lib.NoTrainingMove = false; break;
                case "FirstKeyMove": lib.AllKeyMovesTraining = false; lib.NoTrainingMove = false; break;
                case "None": lib.AllKeyMovesTraining = false; lib.NoTrainingMove = true; break;
            }

            var (pgn, courseName) = await Task.Run(() => lib.GetCourse(bid, useLocalData: true));
            var lnCount = data?.ChapterList.Sum(c => c.ResponseLineList.Count) ?? 0;
            if (lnCount > job.LinesTotal) job.LinesTotal = lnCount; // tatsächliche Zahl ist autoritativ
            job.Complete(pgn, courseName, data?.ChapterList.Count ?? 0, lnCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Course fetch job {JobId} failed for bid {Bid}", jobId, bid);
            job.Fail(ex.Message);
        }
    }
}
