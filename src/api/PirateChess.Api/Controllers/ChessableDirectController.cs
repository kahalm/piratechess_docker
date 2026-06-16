using Microsoft.AspNetCore.Mvc;
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
    private readonly ILogger<ChessableDirectController> _logger;

    public ChessableDirectController(
        IChessableHttpService chessableHttp,
        CourseFetchJobStore jobStore,
        RawCourseCache rawCache,
        ILogger<ChessableDirectController> logger)
    {
        _chessableHttp = chessableHttp;
        _jobStore = jobStore;
        _rawCache = rawCache;
        _logger = logger;
    }

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

        var dto = new DirectCourseProgressResponse(
            job.Status, job.ChaptersDone, job.ChaptersTotal, job.LinesDone,
            job.ChapterCount, job.LineCount, job.CourseName,
            job.Status == "completed" ? job.Pgn : null, job.Error);

        if (job.Status is "completed" or "failed")
            _jobStore.Remove(jobId); // einmaliger Terminal-Read

        return Ok(dto);
    }

    private async Task RunFetchAsync(string jobId, string bearer, string uid, string bid, string mode)
    {
        var job = _jobStore.Get(jobId);
        if (job is null) return;
        try
        {
            // Rohdaten aus dem (kurs-/bid-weiten) Cache wiederverwenden → kein Chessable-Call,
            // auch wenn ein anderer User denselben Kurs schon geholt hat.
            var data = await _rawCache.GetAsync(bid);
            if (data is not null)
            {
                job.ChaptersTotal = data.ChapterList.Count;
                job.ChaptersDone = data.ChapterList.Count;
                job.LinesDone = data.ChapterList.Sum(c => c.ResponseLineList.Count);
            }
            else
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
                    });

                if (fetchError is not null)
                {
                    job.Status = "failed";
                    job.Error = fetchError.Trim() is "{}" or "" ? "Invalid bearer" : fetchError;
                    return;
                }
                data = fetched;
                if (data is not null) await _rawCache.SetAsync(bid, data);
            }

            var lib = new piratechess_lib.PirateChessLib { restResponseCourse = data };
            switch (mode)
            {
                case "AllKeyMoves": lib.AllKeyMovesTraining = true; lib.NoTrainingMove = false; break;
                case "FirstKeyMove": lib.AllKeyMovesTraining = false; lib.NoTrainingMove = false; break;
                case "None": lib.AllKeyMovesTraining = false; lib.NoTrainingMove = true; break;
            }

            var (pgn, courseName) = await Task.Run(() => lib.GetCourse(bid, useLocalData: true));
            job.ChapterCount = data?.ChapterList.Count ?? 0;
            job.LineCount = data?.ChapterList.Sum(c => c.ResponseLineList.Count) ?? 0;
            job.CourseName = courseName;
            job.Pgn = pgn;
            job.Status = "completed";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Course fetch job {JobId} failed for bid {Bid}", jobId, bid);
            job.Status = "failed";
            job.Error = ex.Message;
        }
    }
}
