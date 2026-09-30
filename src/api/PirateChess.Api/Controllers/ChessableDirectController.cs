using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
// Klassenweit kleines Body-Limit: direct-Requests tragen nur Bearer + bid + Mode (wenige KB) — 256 KB
// lässt reichlich Luft und kappt Body-Fluten. Ausnahme: course/parse (browser-erfasste Roh-Kurse,
// eigenes größeres Limit an der Action). Dazu ein Fixed-Window-Limiter (Policy "direct", Program.cs):
// großzügig dimensioniert, damit Fortschritts-Polling + laufende Fetch-Jobs nie abreißen.
[ApiController]
[Route("api/chessable/direct")]
[ServiceKeyAuth]
[RequestSizeLimit(256 * 1024)]
[EnableRateLimiting("direct")]
public class ChessableDirectController : ControllerBase
{
    private readonly IChessableHttpService _chessableHttp;
    private readonly CourseFetchJobStore _jobStore;
    private readonly RawCourseCache _rawCache;
    private readonly RawLineCache _lineCache;
    private readonly CourseDataProvider _courseData;
    private readonly BrowserCourseParseService _browserParse;
    private readonly RawCourseReconstructor _reconstructor;
    private readonly VpnIpHealth _ipHealth;
    private readonly IVpnRotationService _vpn;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ChessableDirectController> _logger;

    public ChessableDirectController(
        IChessableHttpService chessableHttp,
        CourseFetchJobStore jobStore,
        RawCourseCache rawCache,
        RawLineCache lineCache,
        CourseDataProvider courseData,
        BrowserCourseParseService browserParse,
        RawCourseReconstructor reconstructor,
        VpnIpHealth ipHealth,
        IVpnRotationService vpn,
        IHostApplicationLifetime lifetime,
        ILogger<ChessableDirectController> logger)
    {
        _chessableHttp = chessableHttp;
        _jobStore = jobStore;
        _rawCache = rawCache;
        _lineCache = lineCache;
        _courseData = courseData;
        _browserParse = browserParse;
        _reconstructor = reconstructor;
        _ipHealth = ipHealth;
        _vpn = vpn;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Chessable-Kurs-IDs sind numerisch. bid VOR Cache-Lock/Fetch gegen dieses Format prüfen:
    /// verhindert, dass beliebige (ungültige) Strings Per-bid-Locks im <see cref="RawCourseCache"/>
    /// anlegen (die Einträge räumen sich zwar per Refcount wieder ab, aber Müll-bids sollen gar nicht
    /// erst bis dorthin kommen) und teure Chessable-Abrufe auslösen.</summary>
    private static bool IsValidBid(string? bid)
        => !string.IsNullOrEmpty(bid) && bid.Length <= 12 && bid.All(char.IsAsciiDigit);

    /// <summary>Per-IP-Auswertung: wie viele Requests/Blocks pro VPN-Ausgangs-IP (über alle Rotationen),
    /// schlechteste zuerst. Für „welche IP ist immer wieder schlecht".</summary>
    [HttpGet("debug/ip-health")]
    public IActionResult IpHealth() => Ok(_ipHealth.Snapshot());

    /// <summary>Liste der VPN-Tunnel im Pool (Index, Proxy, Status) — woraus der Pin-Test wählen kann.</summary>
    [HttpGet("vpn/tunnels")]
    public IActionResult Tunnels() => Ok(_vpn.DescribeTunnels());

    /// <summary>Commit-SHA + Ref des laufenden Images (vom CI als Build-Arg gesetzt, siehe Dockerfile
    /// <c>ARG GIT_SHA</c>/<c>GIT_REF</c> → ENV <c>BUILD_GIT_SHA</c>/<c>BUILD_GIT_REF</c>). RookHubs
    /// Admin-CI-Seite ruft das ab, um den GitHub-Actions-Run des laufenden piratechess-Images zu markieren
    /// (Branch bei :dev, Tag bei :prod). Leere Strings, wenn nicht gesetzt.</summary>
    [HttpGet("build-info")]
    public IActionResult BuildInfo() => Ok(new
    {
        sha = Environment.GetEnvironmentVariable("BUILD_GIT_SHA") ?? "",
        @ref = Environment.GetEnvironmentVariable("BUILD_GIT_REF") ?? "",
    });

    /// <summary>Bearer-Test (getHomeData). Mit <c>TunnelIndex</c> (0-basiert) läuft der Test fix über
    /// GENAU diesen VPN-Tunnel — für „funktioniert Chessable über genau diesen VPN / mit welcher Exit-IP".
    /// Ohne <c>TunnelIndex</c> wie bisher über das round-robin.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] DirectBearerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Bearer))
            return BadRequest(new { message = "Bearer is required" });

        var pin = request.TunnelIndex;
        if (pin is int idx && (idx < 0 || idx >= _vpn.TunnelCount))
            return BadRequest(new { message = $"Ungültiger Tunnel-Index {idx}. Verfügbar: 0..{_vpn.TunnelCount - 1}." });

        var (uid, uidError) = _chessableHttp.ExtractUidFromBearer(request.Bearer);
        if (uidError is not null)
            return BadRequest(new { message = uidError });

        var (courses, error) = await _chessableHttp.GetCoursesAsync(request.Bearer, uid, ct, pin);
        if (error is not null)
        {
            var cleanMessage = error.Trim() is "{}" or "" ? "Invalid bearer" : error;
            return BadRequest(new { message = cleanMessage });
        }

        // Bei gepinntem Test zusätzlich melden, über welchen Tunnel + welche Exit-IP getestet wurde.
        string? proxy = null, exitIp = null;
        if (pin is int pinned)
        {
            proxy = _vpn.DescribeTunnels().FirstOrDefault(t => t.Index == pinned)?.ProxyUrl;
            try { exitIp = await _vpn.GetTunnelPublicIpAsync(pinned, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Exit-IP für Tunnel {Pin} nicht ermittelbar", pinned); }
        }

        return Ok(new DirectTestResponse(uid, courses!.Count, pin, proxy, exitIp));
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
        if (!IsValidBid(request?.Bid))
            return BadRequest(new { message = "Invalid bid" });

        if (!TrainingModes.TryParseOrDefault(request!.Mode, TrainingMode.FirstKeyMove, out var mode))
            return BadRequest(new { message = TrainingModes.InvalidModeMessage });

        // Einheitliche Regel aller Kurs-Endpoints (Course/CourseInfo/StartCourse): Bearer ist NUR für
        // den echten Chessable-Abruf (Cache-Miss) nötig — ein gecachter Kurs wird auch ohne bedient.
        var bearer = request.Bearer ?? string.Empty;
        string uid = string.Empty;
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            var (u, uidError) = _chessableHttp.ExtractUidFromBearer(bearer);
            if (uidError is not null)
                return BadRequest(new { message = uidError });
            uid = u;
        }

        // ForceRefresh: gecachte Rohdaten überspringen → echter Neu-Abruf (Cache wird erst bei Erfolg ersetzt).
        var fetch = await _courseData.GetOrFetchAsync(request.Bid, bearer, uid, request.ForceRefresh, ct: ct);
        if (fetch.BearerMissing)
            return BadRequest(new { message = "Bearer is required" });
        if (fetch.FetchError is { } cleanMessage)
        {
            _logger.LogWarning("Course fetch failed for bid {Bid} (uid {Uid}): {Error}", request.Bid, uid, cleanMessage);
            return BadRequest(new { message = cleanMessage });
        }
        var data = fetch.Data;

        var lib = new piratechess_lib.PirateChessLib { restResponseCourse = data };
        mode.ApplyTo(lib);

        lib.SetErrorDiagEvent(detail =>
            _logger.LogWarning("Chessable-Parser übersprang eine Linie/Kapitel (bid {Bid}, uid {Uid}): {Detail}", request.Bid, uid, detail));

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
        if (lib.ErrorCount > 0)
        {
            _logger.LogWarning("Kurs bid {Bid} (uid {Uid}) mit {Errors} übersprungenen Linien/Kapiteln exportiert (Details siehe vorige Warnungen)", request.Bid, uid, lib.ErrorCount);
        }

        var chapterCount = data?.ChapterList.Count ?? 0;
        var lineCount = data?.ChapterList.Sum(c => c.ResponseLineList.Count) ?? 0;

        return Ok(new DirectCourseResponse(request.Bid, courseName, mode.ToString(), chapterCount, lineCount, pgn));
    }

    /// <summary>
    /// Fetch-freier Parse: nimmt bereits (vom Browser über die RepCheck-Extension) erfasstes rohes
    /// Chessable-JSON — je Kapitel die getList-Antwort (<c>ChapterJson</c>) plus die getGame-Antworten
    /// (<c>Lines</c>) in getList-Reihenfolge — und erzeugt daraus dasselbe PGN wie der Live-Abruf, OHNE
    /// Chessable-Kontakt/VPN und OHNE Bearer. Der Browser hat die Daten als echte eingeloggte Session
    /// geholt (passiert Cloudflare); piratechess parst hier nur. <c>Mode</c> steuert die Trainingsannotation
    /// wie bei <see cref="Course"/> ("None"→Repertoire, "FirstKeyMove"→Buch, "AllKeyMoves").
    /// </summary>
    // Eigenes, größeres Body-Limit (überschreibt das klassenweite 256-KB-Limit): hier kommt das rohe
    // Chessable-JSON GANZER Kurse an (36+ MB dokumentiert, s. RawCourseCache) — Kestrels 28,6-MB-Default
    // bzw. das Mini-Limit würden den Browser-Import großer Kurse abschneiden. 100 MB als harte Obergrenze.
    [HttpPost("course/parse")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<IActionResult> ParseCourse([FromBody] DirectCourseParseRequest request, CancellationToken ct)
    {
        if (!IsValidBid(request?.Bid))
            return BadRequest(new { message = "Invalid bid" });

        if (!TrainingModes.TryParseOrDefault(request!.Mode, TrainingMode.None, out var mode))
            return BadRequest(new { message = TrainingModes.InvalidModeMessage });

        var chapters = request.Chapters ?? [];
        if (chapters.Count == 0)
            return BadRequest(new { message = "At least one chapter with captured lines is required" });
        var shapeError = BrowserCourseAssembler.Validate(chapters);
        if (shapeError is not null)
            return BadRequest(new { message = shapeError });

        var (response, error) = await _browserParse.ParseAsync(request, chapters, mode, ct);
        return response is null ? BadRequest(new { message = error }) : Ok(response);
    }

    /// <summary>
    /// Welche Linien (oids) liegen schon im geteilten Rohdaten-Cache? Nur die Existenz, nie der Inhalt — die
    /// RepCheck-Extension überspringt für diese Linien den Chessable-Abruf und schickt beim Import nur die oid;
    /// den Inhalt setzt <see cref="ParseCourse"/> serverseitig ein. Mit <c>Bid</c> nur Linien, mit denen
    /// <see cref="ParseCourse"/> einen Import dieses Kurses auch füllt.
    /// </summary>
    [HttpPost("lines/cached")]
    public async Task<IActionResult> LinesCached([FromBody] DirectCachedLinesRequest request, CancellationToken ct)
    {
        if (request?.Bid is not null && !IsValidBid(request.Bid))
            return BadRequest(new { message = "Invalid bid" });
        var raw = request?.Oids ?? [];
        if (raw.Count > BrowserCourseAssembler.MaxOidsPerLookup)
            return BadRequest(new { message = $"At most {BrowserCourseAssembler.MaxOidsPerLookup} oids per request" });
        var oids = new List<int>(raw.Count);
        foreach (var o in raw)
        {
            if (!BrowserCourseAssembler.TryParseOid(o, out var oid))
                return BadRequest(new { message = "Invalid oid" });
            oids.Add(oid);
        }
        var cached = await _lineCache.GetCachedOidsAsync(oids, request?.Bid, ct);
        return Ok(new DirectCachedLinesResponse(cached.Select(o => o.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList()));
    }

    /// <summary>
    /// Wartung des geteilten Linien-Caches: prüft Linien so, wie der Parser sie liest. Ungültige werden markiert statt
    /// gelöscht (Inhalt bleibt), markierte, die inzwischen gültig sind — etwa nach einer Korrektur der Prüfung oder
    /// des Parsers —, werden wieder freigegeben. <c>all=false</c> (Standard): nur markierte Linien; <c>all=true</c>:
    /// der ganze Cache (am 2026-09-15 rund 168 000 Linien / 4,9 GB, dauert Minuten). <c>dryRun=true</c> (Standard):
    /// nur Bericht.
    /// </summary>
    [HttpPost("lines/revalidate")]
    public async Task<IActionResult> RevalidateLines([FromQuery] bool dryRun = true, [FromQuery] bool all = false, CancellationToken ct = default)
    {
        var r = await _lineCache.RevalidateAsync(all, apply: !dryRun, ct);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return Ok(new DirectLineRevalidationResponse(!dryRun, r.Checked, r.NewlyInvalidCount, r.ClearedCount, r.StillInvalid,
            r.NewlyInvalid.Select(x => new DirectInvalidLine(x.Oid.ToString(inv), x.Reason)).ToList(),
            r.Cleared.Select(o => o.ToString(inv)).ToList()));
    }

    /// <summary>
    /// Startet den tiefen Kurs-Abruf asynchron und liefert eine JobId. Der Fortschritt
    /// (Kapitel/Linien) ist über <c>GET /api/chessable/direct/course/{jobId}</c> abrufbar; dort
    /// kommt bei Status "completed" auch das fertige PGN. Für Fortschrittsanzeige in rookhub.
    /// </summary>
    /// <summary>Ob die Rohdaten dieses Kurses schon gecacht sind (→ Import braucht keinen Chessable-Abruf).</summary>
    [HttpGet("course/{bid}/cached")]
    public async Task<IActionResult> CourseCached(string bid, CancellationToken ct)
        => IsValidBid(bid)
            ? Ok(new { cached = await _rawCache.ExistsAsync(bid, ct) })
            : BadRequest(new { message = "Invalid bid" });

    /// <summary>Wartung/Force-Refresh: verwirft die gecachten Rohdaten eines Kurses (Struktur + Linien),
    /// damit der nächste Abruf ihn wirklich neu von Chessable holt. Ohne das bedient jeder Cache-Treffer
    /// dauerhaft den Stand des Erst-Imports — Kurs-Updates des Autors kämen nie an.</summary>
    [HttpDelete("course/{bid}/cache")]
    public async Task<IActionResult> DeleteCourseCache(string bid, CancellationToken ct)
    {
        if (!IsValidBid(bid))
            return BadRequest(new { message = "Invalid bid" });
        var (removed, lines) = await _rawCache.DeleteAsync(bid, ct);
        return Ok(new { removed, lines });
    }

    /// <summary>Alle gecachten Kurs-Bids auf einmal — rookhub reichert damit die Kursliste mit einem
    /// „gecacht/sofort verfügbar"-Flag an (1 Call statt N).</summary>
    [HttpGet("courses/cached")]
    public async Task<IActionResult> CachedBids(CancellationToken ct)
        => Ok(new { bids = (await _rawCache.GetAllCachedBidsAsync(ct)).ToList() });

    /// <summary>Wartung: baut den servable Cache eines Kurses aus BEREITS GESPEICHERTEN Rohdaten
    /// wieder auf (Audit-Log + permanenter Linien-Cache) — ohne Chessable-Abruf. Für Kurse, deren
    /// aktueller Bearer sie nicht besitzt (BOOK_NOT_OWNED), deren Rohantworten aber noch vorliegen.</summary>
    [HttpPost("course/reconstruct")]
    public async Task<IActionResult> Reconstruct([FromBody] DirectCourseReconstructRequest request, CancellationToken ct)
    {
        if (!IsValidBid(request?.Bid))
            return BadRequest(new { message = "Invalid bid" });
        var r = await _reconstructor.ReconstructAsync(request!.Bid, ct);
        return r.Ok
            ? Ok(new { ok = true, chapters = r.Chapters, lines = r.Lines, missingLines = r.MissingLines, unparseableLines = r.UnparseableLines })
            : BadRequest(new { message = r.Error, chapters = r.Chapters, lines = r.Lines, missingLines = r.MissingLines, unparseableLines = r.UnparseableLines });
    }

    /// <summary>Leichte Vorab-Schätzung der Gesamt-Linienzahl eines Kurses (für die Admin-Kursliste).
    /// Gecacht → aus dem Rohdaten-Cache (kein Chessable-Call); sonst EIN getCourse?includeVariations.</summary>
    [HttpPost("course/info")]
    public async Task<IActionResult> CourseInfo([FromBody] DirectCourseRequest request, CancellationToken ct)
    {
        if (!IsValidBid(request?.Bid))
            return BadRequest(new { message = "Invalid bid" });

        // Gecacht → Gesamtzahl ohne Chessable-Abruf aus den Rohdaten. Bearer wird — wie bei
        // Course/StartCourse — erst beim Cache-Miss verlangt (echter Chessable-Abruf nötig).
        var cached = await _rawCache.GetAsync(request!.Bid, ct);
        if (cached is not null)
            return Ok(new DirectCourseInfoResponse(request.Bid, cached.ChapterList.Sum(c => c.ResponseLineList.Count), true));

        if (string.IsNullOrWhiteSpace(request.Bearer))
            return BadRequest(new { message = "Bearer is required" });
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
    public async Task<IActionResult> StartCourse([FromBody] DirectCourseRequest request, CancellationToken ct)
    {
        if (!IsValidBid(request?.Bid))
            return BadRequest(new { message = "Invalid bid" });

        if (!TrainingModes.TryParseOrDefault(request!.Mode, TrainingMode.FirstKeyMove, out var mode))
            return BadRequest(new { message = TrainingModes.InvalidModeMessage });

        // Bearer ist NUR für den echten Chessable-Abruf (Cache-Miss) nötig. Liegen die Rohdaten des
        // Kurses bereits im (bid-weiten) Cache, kann RunFetchAsync sie ohne Chessable-Kontakt/uid
        // ausliefern → dann darf der Bearer fehlen (z. B. Admin-Re-Fetch eines Repertoires, dessen
        // Besitzer keinen Bearer (mehr) hinterlegt hat, der Kurs aber von jemand anderem gecacht wurde).
        var bearer = request.Bearer ?? string.Empty;
        string uid = string.Empty;
        piratechess_lib.RestResponseCourse? cached = null;
        if (string.IsNullOrWhiteSpace(bearer))
        {
            // Force-Refresh heißt echter Chessable-Abruf → der Cache-Fallback greift hier nicht.
            if (request.ForceRefresh || (cached = await _rawCache.GetAsync(request.Bid, ct)) is null)
                return BadRequest(new { message = "Bearer is required" });
            // gecacht → uid bleibt leer (im Cache-Pfad ungenutzt); der Job nimmt die eben geladenen Daten,
            // statt den ganzen Kurs gleich noch einmal zu laden und zu entpacken (S2-018).
        }
        else
        {
            var (u, uidError) = _chessableHttp.ExtractUidFromBearer(bearer);
            if (uidError is not null)
                return BadRequest(new { message = uidError });
            uid = u;
        }

        var jobId = Guid.NewGuid().ToString("N");
        _jobStore.Create(jobId);
        // Fire-and-forget: _chessableHttp + _jobStore sind Singletons → nach Controller-Dispose gültig.
        // Abbruch über DELETE course/{jobId} oder beim Container-Stopp (siehe RunFetchAsync).
        _ = Task.Run(() => RunFetchAsync(jobId, bearer, uid, request.Bid, mode, request.ForceRefresh, cached));
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

        if (s.Status is "completed" or "failed" or "cancelled")
            _jobStore.Remove(jobId); // einmaliger Terminal-Read

        return Ok(dto);
    }

    /// <summary>Bricht einen Kurs-Abruf-Job ab (S2-008) und gibt ihn frei, auch ein schon fertiges PGN. Der
    /// laufende Chessable-Abruf endet sofort (ein laufender curl wird beendet), es geht kein weiterer Request
    /// über die VPN-IP. Für rookhub bei Abbruch, Pause oder erkanntem Stillstand des Imports.</summary>
    [HttpDelete("course/{jobId}")]
    public IActionResult CancelCourse(string jobId)
    {
        var job = _jobStore.Get(jobId);
        if (job is null) return NotFound(new { message = "Job not found" });
        var cancelled = job.Cancel();
        _jobStore.Remove(jobId);
        return Ok(new { cancelled });
    }

    /// <param name="preloaded">Im Start-Gate schon geladene Rohdaten (nur ohne Bearer und ohne Force-Refresh).</param>
    private async Task RunFetchAsync(string jobId, string bearer, string uid, string bid, TrainingMode mode, bool forceRefresh = false,
        piratechess_lib.RestResponseCourse? preloaded = null)
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
            var fetch = await _courseData.GetOrFetchAsync(bid, bearer, uid, forceRefresh, preloaded,
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
            var data = fetch.Data;

            if (data is not null)
            {
                job.ChaptersTotal = data.ChapterList.Count;
                job.ChaptersDone = data.ChapterList.Count;
                job.LinesDone = data.ChapterList.Sum(c => c.ResponseLineList.Count);
                job.LinesTotal = job.LinesDone; // gecacht/fertig → vollständige Zahl
            }

            var lib = new piratechess_lib.PirateChessLib { restResponseCourse = data };
            mode.ApplyTo(lib);

            lib.SetErrorDiagEvent(detail =>
                _logger.LogWarning("Chessable-Parser übersprang eine Linie/Kapitel (job {JobId}, bid {Bid}): {Detail}", jobId, bid, detail));
            ct.ThrowIfCancellationRequested();
            var (pgn, courseName) = await Task.Run(() => lib.GetCourse(bid, useLocalData: true), ct);
            if (lib.ErrorCount > 0)
                _logger.LogWarning("Kurs-Fetch-Job {JobId} bid {Bid} mit {Errors} übersprungenen Linien/Kapiteln abgeschlossen", jobId, bid, lib.ErrorCount);
            var lnCount = data?.ChapterList.Sum(c => c.ResponseLineList.Count) ?? 0;
            if (lnCount > job.LinesTotal) job.LinesTotal = lnCount; // tatsächliche Zahl ist autoritativ
            job.Complete(pgn, courseName, data?.ChapterList.Count ?? 0, lnCount);
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
}
