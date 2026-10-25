using System.Text.Json;
using Serilog.Context;
using piratechess_lib;
using static PirateChess.Api.Services.ChessableResponseClassifier;

namespace PirateChess.Api.Services;

/// <summary>
/// Der tiefe Kursabruf: Kursstruktur (getCourse) → je Kapitel getList → je Linie getGame, mit Retry, Prüfregel je
/// Antwort, Linien-Resume-Cache und Abbruch bei Chessable-Fehlerkörpern. Aus <see cref="ChessableHttpService"/>
/// herausgelöst (Zerlegung Z46); loggt unter dessen Kategorie, damit Log-Filter gleich bleiben.
/// </summary>
internal sealed class ChessableCourseFetcher
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Der Kurs-Struktur-Abruf hatte bisher keinen Retry. Direkt nach einer VPN-
    // Rotation liefert der gluetun-Proxy kurz 503 (CONNECT tunnel failed) → ein
    // einziges 503 ließ den ganzen Import scheitern. Daher bounded Retry mit Pause,
    // bis der Tunnel wieder steht.
    private const int CourseFetchAttempts = 4;

    // Der Kapitel-Abruf (getList) hatte bisher weder Validierung noch Retry: ein mitten im
    // Stream abgebrochener Body (~8 KB-Truncation durch den VPN-Proxy) ist nicht-leer, aber
    // unvollständig → er parst NICHT als ResponseChapter, wurde aber truncated gecacht und ließ
    // beim Replay (lib.GetCourse) JsonSerializer crashen → der ganze Kurs-Import scheiterte.
    // Daher abgeschnittene Kapitel sofort im selben Lauf neu vom Server holen.
    private const int ChapterFetchAttempts = 4;

    private readonly ILogger<ChessableHttpService> _logger;
    private readonly ChessableCurlTransport _transport;
    private readonly RawLineCache _lineCache;
    private readonly int _delayMinMs;
    private readonly int _delayMaxMs;
    private readonly int _parallelLineFetches;
    private readonly int _blockRetryDelayMs;

    /// <summary>Pause vor dem Retry des Struktur-/Kapitelabrufs (Tests setzen 0).</summary>
    internal int ProxyRetryDelayMs { get; set; } = 4000;

    public ChessableCourseFetcher(ILogger<ChessableHttpService> logger, ChessableCurlTransport transport, RawLineCache lineCache,
        int delayMinMs, int delayMaxMs, int parallelLineFetches, int blockRetryDelayMs)
    {
        _logger = logger;
        _transport = transport;
        _lineCache = lineCache;
        _delayMinMs = delayMinMs;
        _delayMaxMs = delayMaxMs;
        _parallelLineFetches = parallelLineFetches;
        _blockRetryDelayMs = blockRetryDelayMs;
    }

    /// <summary>Zustand eines Kursabrufs, den alle Kapitel und (parallelen) Linien teilen.</summary>
    private sealed class CourseRun(string bearer, string uid, string bid, CourseFetchOptions options, CancellationToken ct)
    {
        public string Bearer { get; } = bearer;
        public string Uid { get; } = uid;
        public string Bid { get; } = bid;
        public CourseFetchOptions Options { get; } = options;
        public CancellationToken Ct { get; } = ct;
        public int CumLines;
    }

    /// <summary>Zustand der Linien eines Kapitels: Plätze in Original-Reihenfolge, Zähler, erster Abbruchgrund.</summary>
    private sealed class ChapterRun(int chapterIdx, List<Line> lines)
    {
        public int ChapterIdx { get; } = chapterIdx;
        public List<Line> Lines { get; } = lines;
        public RestResponseLine[] Slots { get; } = new RestResponseLine[lines.Count];
        public int Done;
        // Erster Grund, den ganzen Abruf abzubrechen (Chessable-Fehlerkörper bei einer Linie). Parallele
        // Linien sehen ihn und hören auf; das Kapitel wird danach nicht weiterverarbeitet.
        public string? AbortError;
    }

    /// <summary>Zufällige Inter-Request-Pause (0, wenn beide Delays 0). Nur nach echtem Request nötig.</summary>
    private Task RandomDelayAsync(CancellationToken ct) =>
        _delayMaxMs <= 0 ? Task.CompletedTask : Task.Delay(Random.Shared.Next(_delayMinMs, _delayMaxMs), ct);

    public async Task<(RestResponseCourse? data, string? error)> FetchAsync(
        string bearer, string uid, string bid, CourseFetchOptions options, CancellationToken ct)
    {
        // Alle Lifecycle-Logs dieses Scrapes (Retries/Warnungen/Per-Request) für die
        // zentrale Kibana-Filterung mit dem Domänen-Tag versehen → ECS `tags`.
        using var _tagScope = LogContext.PushProperty("LogTags", "chessable,scrape");

        var (course, courseContent, structureError) = await FetchStructureAsync(bearer, uid, bid, ct);
        if (structureError is not null)
            return (null, structureError);
        var chapters = course!.Course.Data;

        // Gesamt-Linienzahl direkt aus der getCourse-Antwort (includeVariations) melden — bekannt
        // VOR den teuren per-Linie-getGame-Abrufen. Summe der Varianten je Kapitel (Fallback: "total").
        var totalLines = chapters.Sum(c => c.Variations.Count > 0 ? c.Variations.Count : c.Total);
        if (totalLines > 0) options.OnTotalLines?.Invoke(totalLines);

        var restResponseCourse = new RestResponseCourse
        {
            CourseJsonContent = courseContent
        };
        var run = new CourseRun(bearer, uid, bid, options, ct);

        // 2. Fetch each chapter
        for (int chapterIdx = 0; chapterIdx < chapters.Count; chapterIdx++)
        {
            ct.ThrowIfCancellationRequested();

            var chapter = chapters[chapterIdx];
            options.OnChapterProgress?.Invoke($"{chapterIdx + 1} / {chapters.Count}");

            var (chapterContent, responseChapter, chapterError) = await FetchChapterAsync(run, chapter.Id, chapterIdx);
            if (chapterError is not null)
                return AbortFetch(bid, chapterError);

            var restResponseChapter = new RestResponseChapter
            {
                ChapterJsonContent = chapterContent
            };

            var lineError = await FetchLinesAsync(run, new ChapterRun(chapterIdx, responseChapter!.List.Data), restResponseChapter);
            if (lineError is not null)
                return AbortFetch(bid, lineError);

            restResponseCourse.ChapterList.Add(restResponseChapter);

            // Random delay between chapter requests
            if (chapterIdx < chapters.Count - 1)
                await RandomDelayAsync(ct);
        }

        return (restResponseCourse, null);
    }

    /// <summary>
    /// 1. Fetch course structure — der gluetun-Proxy liefert direkt nach einer
    ///    VPN-Rotation kurz 503 (CONNECT tunnel failed). Anders als der Line-Fetch
    ///    hatte dieser Aufruf bisher keinen Retry → ein einziges 503 ließ den
    ///    ganzen Import mit "Empty course response" scheitern. Daher bounded Retry.
    /// includeVariations=true → die getCourse-Antwort enthält je Kapitel die Varianten(-Liste) inkl.
    /// Anzahl → wir kennen die Gesamt-Linienzahl SOFORT (vor den teuren getGame-Abrufen) für
    /// Fortschritt/ETA. Ändert die gecachte Kurs-Rohdaten (größer); der Parser ignoriert Zusatzfelder.
    /// </summary>
    private async Task<(ResponseCourse? course, string content, string? error)> FetchStructureAsync(
        string bearer, string uid, string bid, CancellationToken ct)
    {
        var courseUrl = $"https://www.chessable.com/api/v1/getCourse?uid={uid}&bid={bid}&includeVariations=true";
        string courseContent = "";
        for (int attempt = 0; attempt < CourseFetchAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                courseContent = await _transport.GetAsync(courseUrl, bearer, "course", uid, ct);
            }
            catch (ProxyTunnelException ex)
            {
                if (attempt < CourseFetchAttempts - 1)
                {
                    _logger.LogWarning(
                        "Course fetch hit proxy tunnel 503 (VPN reconnecting), retry {Attempt}/{Total}",
                        attempt + 1, CourseFetchAttempts);
                    await Task.Delay(ProxyRetryDelayMs, ct);
                    continue;
                }
                return (null, courseContent, $"Failed to fetch course (proxy tunnel unavailable): {ex.Message}");
            }
            catch (Exception ex)
            {
                return (null, courseContent, $"Failed to fetch course: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(courseContent) && courseContent != "{}")
                break;

            // Leerer Body ohne Exception (Proxy gab leere Antwort zurück): kurz warten, erneut.
            if (attempt < CourseFetchAttempts - 1)
            {
                _logger.LogWarning("Empty course response, retry {Attempt}/{Total}", attempt + 1, CourseFetchAttempts);
                await Task.Delay(ProxyRetryDelayMs, ct);
            }
        }

        return (ParseStructure(courseContent, bearer, out var error), courseContent, error);
    }

    /// <summary>Prüft und parst den getCourse-Body; bei Fehler null und die Meldung in <paramref name="error"/>.</summary>
    private static ResponseCourse? ParseStructure(string courseContent, string bearer, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(courseContent) || courseContent == "{}")
        {
            error = "Empty course response";
            return null;
        }

        // Abgelaufener Bearer-Fehler-Body bzw. HTML-Seite (Token/Cloudflare/Proxy) → sprechende
        // Meldung statt „Failed to parse course JSON" bzw. dem rohen JSON-Parser-Text; dabei
        // Token-Ablauf von (vermutlich VPN-IP-)Block unterscheiden.
        if (TryGetChessableErrorMessage(courseContent) is { } courseApiError)
            error = courseApiError;
        else if (LooksLikeHtml(courseContent))
            error = ClassifyBlockedResponse(courseContent, bearer);
        if (error is not null)
            return null;

        ResponseCourse? course;
        try
        {
            course = JsonSerializer.Deserialize<ResponseCourse>(courseContent, JsonOpts);
        }
        catch
        {
            error = "Failed to parse course JSON";
            return null;
        }

        if (course?.Course?.Data is null || course.Course.Data.Count == 0)
        {
            error = "Course has no chapters";
            return null;
        }
        return course;
    }

    /// <summary>
    /// Kapitel-Struktur (getList) mit Validierung + Retry holen: nur ein Body, der die gemeinsame
    /// Prüfregel besteht (ChessableContent.ChapterReason), wird akzeptiert. Ein leerer/abgeschnittener
    /// Body wird im selben Lauf erneut vom Server geholt, statt ihn truncated weiterzuverarbeiten und
    /// zu cachen (sonst Kurs lückenhaft bzw. Crash beim Replay). Ein Chessable-Fehlerkörper (HTTP 200)
    /// ist ein Fehlversuch; betrifft er das Token, bricht der Abruf sofort ab, sonst nach dem letzten
    /// Versuch — nie wird er als Kapitel ohne Linien weitergereicht.
    /// </summary>
    private async Task<(string content, ResponseChapter? chapter, string? abortError)> FetchChapterAsync(
        CourseRun run, int chapterId, int chapterIdx)
    {
        var ct = run.Ct;
        var chapterUrl = $"https://www.chessable.com/api/v1/getList?uid={run.Uid}&bid={run.Bid}&lid={chapterId}";
        string chapterContent = "";
        ResponseChapter? responseChapter = null;
        for (int attempt = 0; attempt < ChapterFetchAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                chapterContent = await _transport.GetAsync(chapterUrl, run.Bearer, "chapter", run.Uid, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch chapter {ChapterId}, attempt {Attempt}", chapterId, attempt + 1);
                chapterContent = "";
            }

            responseChapter = TryParseChapter(chapterContent);
            if (responseChapter is not null)
                break; // vollständig geparst → ok

            var chapterApiError = TryGetChessableErrorMessage(chapterContent ?? "", out var chapterTokenError);
            if (chapterTokenError)
                return (chapterContent!, null, chapterApiError!);

            if (attempt < ChapterFetchAttempts - 1)
            {
                _logger.LogWarning(
                    "Chapter {ChapterId} empty/truncated (len {Len}), retry {Attempt}/{Total}",
                    chapterId, chapterContent?.Length ?? 0, attempt + 1, ChapterFetchAttempts);
                run.Options.OnRetry?.Invoke($"Kapitel {chapterIdx + 1}: unvollständig, Retry {attempt + 1}/{ChapterFetchAttempts} ...");
                await Task.Delay(ProxyRetryDelayMs, ct);
            }
            else
            {
                if (chapterApiError is not null)
                    return (chapterContent!, null, $"{chapterApiError} (Kapitel {chapterId}, nach {ChapterFetchAttempts} Versuchen)");
                _logger.LogWarning(
                    "Chapter {ChapterId} still empty/truncated after {Total} attempts — skipping (course won't be cached)",
                    chapterId, ChapterFetchAttempts);
                responseChapter = new ResponseChapter();
            }
        }
        return (chapterContent!, responseChapter ?? new ResponseChapter(), null);
    }

    /// <summary>
    /// 3. Fetch each line in the chapter — reihenfolge-erhaltend, optional parallel
    ///    (Chessable:ParallelLineFetches). Die VPN-Rotation ist drain-aware, wechselt die
    ///    IP also nie mitten in einem laufenden Request — daher ist Parallelität block-sicher.
    /// Liefert den Abbruchgrund oder null; ohne Abbruch hängt es die Linien in Original-Reihenfolge an.
    /// </summary>
    private async Task<string?> FetchLinesAsync(CourseRun run, ChapterRun chapter, RestResponseChapter restResponseChapter)
    {
        var ct = run.Ct;
        var lineCount = chapter.Lines.Count;
        if (_parallelLineFetches <= 1)
        {
            for (int lineIdx = 0; lineIdx < lineCount && chapter.AbortError is null; lineIdx++)
            {
                ct.ThrowIfCancellationRequested();
                await FetchLineAsync(run, chapter, lineIdx);
            }
        }
        else
        {
            using var sem = new SemaphoreSlim(_parallelLineFetches);
            var tasks = new List<Task>(lineCount);
            for (int lineIdx = 0; lineIdx < lineCount; lineIdx++)
            {
                ct.ThrowIfCancellationRequested();
                await sem.WaitAsync(ct);
                if (Volatile.Read(ref chapter.AbortError) is not null) { sem.Release(); break; }
                int idx = lineIdx;
                tasks.Add(Task.Run(async () =>
                {
                    try { await FetchLineAsync(run, chapter, idx); }
                    finally { sem.Release(); }
                }, ct));
            }
            await Task.WhenAll(tasks);
        }

        if (chapter.AbortError is not null)
            return chapter.AbortError;

        // In Original-Reihenfolge anhängen (Parallelität ändert die Reihenfolge nicht).
        foreach (var slot in chapter.Slots)
            restResponseChapter.ResponseLineList.Add(slot ?? new RestResponseLine { LineJsonContent = "" });
        return null;
    }

    private async Task FetchLineAsync(CourseRun run, ChapterRun chapter, int lineIdx)
    {
        if (Volatile.Read(ref chapter.AbortError) is not null) return;
        var line = chapter.Lines[lineIdx];
        var lineUrl = $"https://www.chessable.com/api/v1/getGame?lng=en&uid={run.Uid}&oid={line.Id}";
        string round = $"{(chapter.ChapterIdx + 2):000}.{(lineIdx + 2):000}";

        // Resume-Cache: eine schon einmal erfolgreich geholte Linie (oid) wiederverwenden →
        // kein Chessable-Call, keine Inter-Request-Pause. Beim Force-Refresh übergangen —
        // die Linie wird nach erfolgreichem Abruf per Upsert ersetzt. Bewusst UMGEHEN statt
        // vorher löschen: scheitert der Abruf (Block, totes Bearer), bleibt der alte,
        // funktionierende Stand erhalten statt unwiederbringlich weg zu sein.
        string? lineContent = run.Options.BypassLineCache ? null : await _lineCache.GetAsync(line.Id, run.Bid, run.Ct);
        bool fromCache = lineContent is not null;

        if (!fromCache)
        {
            lineContent = await FetchLineWithRetryAsync(run, chapter, line.Id, lineUrl, round);
            if (lineContent is null)
                return; // Abbruch (eigener oder einer parallelen Linie)

            // Nur vollständig parsbare Linien cachen → kein vergifteter Resume-Cache.
            if (LineParses(lineContent))
                await _lineCache.SetAsync(line.Id, lineContent, run.Bid, run.Ct);
        }

        chapter.Slots[lineIdx] = new RestResponseLine { Oid = line.Id, LineJsonContent = lineContent ?? "" };

        run.Options.OnCumulativeLines?.Invoke(Interlocked.Increment(ref run.CumLines).ToString());
        run.Options.OnLineProgress?.Invoke($"{Interlocked.Increment(ref chapter.Done)} / {chapter.Lines.Count}");

        // Inter-Request-Pause nur nach echtem Request (Cache-Treffer braucht keine).
        if (!fromCache) await RandomDelayAsync(run.Ct);
    }

    /// <summary>getGame mit bis zu 10 Versuchen. Liefert den letzten Body, oder null, wenn der Abruf abbricht
    /// (Token tot / Fehlerkörper nach dem letzten Versuch — dann in <see cref="ChapterRun.AbortError"/> — oder eine
    /// parallele Linie hat schon abgebrochen).</summary>
    private async Task<string?> FetchLineWithRetryAsync(CourseRun run, ChapterRun chapter, int lineId, string lineUrl, string round)
    {
        var ct = run.Ct;
        string? lineContent = "";
        for (int attempt = 0; attempt < 10; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                lineContent = await _transport.GetAsync(lineUrl, run.Bearer, "line", run.Uid, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "curl failed for line {LineId}, attempt {Attempt}", lineId, attempt + 1);
                lineContent = "";
            }

            // Nur einen Body akzeptieren, der die gemeinsame Prüfregel besteht — ein abgeschnittener
            // Body, eine Antwort ohne game-Objekt oder ein Chessable-Fehlerkörper würde sonst als
            // "Erfolg" durchgehen und den Cache/Export vergiften.
            if (LineParses(lineContent))
                break;

            // Fehlerkörper (HTTP 200): Token tot → sofort abbrechen, sonst Fehlversuch mit Retry
            // und Abbruch nach dem letzten Versuch. Eine tote Linie kommt leer, nicht als Fehler.
            var lineApiError = TryGetChessableErrorMessage(lineContent ?? "", out var lineTokenError);
            if (lineTokenError || (lineApiError is not null && attempt == 9))
            {
                Interlocked.CompareExchange(ref chapter.AbortError,
                    lineTokenError ? lineApiError : $"{lineApiError} (Linie {lineId}, nach 10 Versuchen)", null);
                return null;
            }
            if (Volatile.Read(ref chapter.AbortError) is not null) return null;

            if (attempt < 9)
            {
                // Der Block hat die IP bereits retired (ReportBlocked im Transport) → der
                // nächste Abruf läuft auf einem frischen Tunnel. Daher nur kurzer
                // Backoff statt der früheren 30 s (größter Zeitfresser des Imports).
                run.Options.OnRetry?.Invoke($"[{round}] Retry {attempt + 1}/10 ...");
                await Task.Delay(_blockRetryDelayMs + Random.Shared.Next(0, 500), ct);
            }
            else
            {
                _logger.LogWarning(
                    "Chessable line fetch gave up after 10 attempts, skipping line {LineId} (round {Round})",
                    lineId, round);
                run.Options.OnRetry?.Invoke($"[{round}] FAILED after 10 attempts, skipping.");
            }
        }
        return lineContent;
    }

    /// <summary>Bricht den Kursabruf mit einer Chessable-Fehlermeldung ab (kein Teil-Kurs, nichts wird gecacht).</summary>
    private (RestResponseCourse? data, string? error) AbortFetch(string bid, string error)
    {
        _logger.LogWarning("Kursabruf bid {Bid} abgebrochen: {Error}", bid, error);
        return (null, error);
    }

    /// <summary>
    /// Parst den getList-Body zu <see cref="ResponseChapter"/>. Liefert null, wenn er die gemeinsame Prüfregel
    /// (<see cref="ChessableContent.ChapterReason"/>) nicht besteht: leer/<c>{}</c>, abgeschnittenes/korruptes
    /// JSON oder ein Chessable-Fehlerkörper → Signal zum Neu-Holen. Ein legitim leeres Kapitel
    /// (<c>{"list":{"data":[]}}</c>) gilt als gültig.
    /// </summary>
    private static ResponseChapter? TryParseChapter(string? content)
    {
        if (ChessableContent.ChapterReason(content) is not null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<ResponseChapter>(content!, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True, wenn der getGame-Body die gemeinsame Prüfregel besteht (<see cref="ChessableContent.LineReason"/>:
    /// parst als <see cref="ResponseLine"/>, trägt ein game-Objekt, ist kein Chessable-Fehlerkörper).</summary>
    private static bool LineParses(string? content) => ChessableContent.LineReason(content) is null;
}
