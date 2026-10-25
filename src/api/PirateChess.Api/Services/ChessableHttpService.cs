using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog.Context;
using piratechess_lib;
using static PirateChess.Api.Services.ChessableCurlArgs;
using static PirateChess.Api.Services.ChessableResponseClassifier;

namespace PirateChess.Api.Services;

public class ChessableHttpService : IChessableHttpService
{
    private readonly ILogger<ChessableHttpService> _logger;
    private readonly RawResponseAudit _audit;
    private readonly IVpnRotationService _vpn;
    // Hinweis: Der zu nutzende Proxy kommt jetzt pro Request aus dem VPN-Lease (Multi-Tunnel),
    // nicht mehr aus einem festen Feld.
    private readonly RawLineCache _lineCache;
    private readonly ICurlRunner _curl;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Der Kurs-Struktur-Abruf hatte bisher keinen Retry. Direkt nach einer VPN-
    // Rotation liefert der gluetun-Proxy kurz 503 (CONNECT tunnel failed) → ein
    // einziges 503 ließ den ganzen Import scheitern. Daher bounded Retry mit Pause,
    // bis der Tunnel wieder steht.
    private const int CourseFetchAttempts = 4;
    internal int ProxyRetryDelayMs { get; init; } = 4000;   // Tests setzen ihn auf 0

    // GESAMT-Timeout je Request (--max-time). Eine hängende IP lieferte den Body sonst erst nach
    // 75–120 s (curl 56), was die serielle Pipeline blockierte. Jetzt bricht der Request nach ~20 s
    // ab → leerer Body → als Soft-Block gewertet → IP retired/rotiert, Tunnel-Health zählt mit.
    // Gesunde Abrufe (auch große Linien/Kapitel) sind <5 s. Konfigurierbar: Chessable:RequestMaxTimeSec.
    private readonly int _requestMaxTimeSec;

    // Der Kapitel-Abruf (getList) hatte bisher weder Validierung noch Retry: ein mitten im
    // Stream abgebrochener Body (~8 KB-Truncation durch den VPN-Proxy) ist nicht-leer, aber
    // unvollständig → er parst NICHT als ResponseChapter, wurde aber truncated gecacht und ließ
    // beim Replay (lib.GetCourse) JsonSerializer crashen → der ganze Kurs-Import scheiterte.
    // Daher abgeschnittene Kapitel sofort im selben Lauf neu vom Server holen.
    private const int ChapterFetchAttempts = 4;

    // Zufälliger Abstand zwischen zwei aufeinanderfolgenden Zeilen-/Kapitel-Requests
    // (menschenähnliches Timing). Prod-Messung 2026-06-15: Chessables Block ist NICHT
    // timing-getrieben sondern requests-pro-IP-getrieben (IP wird nach ~10 Requests
    // soft-geblockt, daher steuert Vpn:RotateAfterRequests die Block-Rate ~0%).
    // Konfigurierbar über Chessable:InterRequestDelayMinMs/MaxMs (zum Beschleunigen runtersetzen).
    private readonly int _delayMinMs;
    private readonly int _delayMaxMs;
    // Wie viele Zeilen eines Kapitels parallel geholt werden (Chessable:ParallelLineFetches).
    // 1 = sequenzielles Verhalten (Default). >1 beschleunigt; die VPN-Rotation ist drain-aware, wechselt
    // also die IP nie mitten in einem laufenden Request (siehe VpnRotationService).
    private readonly int _parallelLineFetches;

    // Backoff vor dem Wiederholungs-Fetch einer leer/{}-geblockten Linie. Früher fix 30 s — das war
    // mit Abstand der größte Zeitfresser (~6 % der Zeilen blocken → ~1,8 s/Zeile amortisiert). Jetzt
    // retired ein Block die IP SOFORT (lease.ReportBlocked → Tunnel rotiert + Pool wechselt), der
    // Retry läuft auf der frischen IP → es genügt ein kurzer Backoff. Konfigurierbar via
    // Chessable:BlockRetryDelayMs.
    private readonly int _blockRetryDelayMs;
    private readonly bool _rotateOnBlock;

    public ChessableHttpService(
        ILogger<ChessableHttpService> logger,
        RawResponseAudit audit,
        IVpnRotationService vpn,
        RawLineCache lineCache,
        ICurlRunner curl,
        IConfiguration configuration)
    {
        _logger = logger;
        _audit = audit;
        _vpn = vpn;
        _lineCache = lineCache;
        _curl = curl;

        // Speed-Stellschrauben (per ENV justierbar). Der Block ist requests-pro-IP-getrieben, NICHT
        // timing-getrieben (Prod-Messung) → der Inter-Request-Delay dient kaum der Block-Vermeidung;
        // Default daher klein gehalten (Block-Vermeidung läuft über die IP-Rotation).
        // 2026-07-12: Default 10x hochgesetzt (200ms -> 2000ms) auf Zuruf, um den Crawl insgesamt
        // weniger aggressiv zu fahren (unabhaengig von der IP-Rotations-Logik oben).
        _delayMinMs = Math.Max(0, configuration.GetValue("Chessable:InterRequestDelayMinMs", 0));
        _delayMaxMs = Math.Max(_delayMinMs + 1, configuration.GetValue("Chessable:InterRequestDelayMaxMs", 2000));
        _parallelLineFetches = Math.Clamp(configuration.GetValue("Chessable:ParallelLineFetches", 1), 1, 16);
        _blockRetryDelayMs = Math.Max(0, configuration.GetValue("Chessable:BlockRetryDelayMs", 1500));
        _requestMaxTimeSec = Math.Clamp(configuration.GetValue("Chessable:RequestMaxTimeSec", 20), 5, 120);
        // Rotation bei IP-Soft-Block. Default true = bisheriges Verhalten (je Block sofort rotieren).
        // Prod fährt das neue Token-gekoppelte Modell: Vpn:RotateOnBlock=false + Vpn:RotateAfterRequests
        // sehr hoch + langsamer Takt → Rotation NUR bei Token-Wechsel (siehe VpnRotationService).
        _rotateOnBlock = configuration.GetValue("Vpn:RotateOnBlock", true);
    }

    /// <summary>Zufällige Inter-Request-Pause (0, wenn beide Delays 0). Nur nach echtem Request nötig.</summary>
    private Task RandomDelayAsync(CancellationToken ct) =>
        _delayMaxMs <= 0 ? Task.CompletedTask : Task.Delay(Random.Shared.Next(_delayMinMs, _delayMaxMs), ct);

    public async Task<(string? jwt, string? error)> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(email))
            return (null, "please fill out email.");
        if (string.IsNullOrEmpty(password))
            return (null, "please fill out password.");

        var hash = ComputeSha512Hash(password);

        var requestBody = new
        {
            method = "email",
            credentials = new { email, password = hash },
            providerData = (object?)null,
            mode = "login",
            checkoutData = (object?)null,
            preferredLanguage = "en",
            newsletterChecked = false
        };
        var json = JsonSerializer.Serialize(requestBody);

        string content;
        try
        {
            content = await CurlPostAsync("https://www.chessable.com/api/v1/authenticate", json, "login", null, ct);
        }
        catch (Exception ex)
        {
            return (null, $"curl failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(content) || content == "{}")
            return (null, content ?? "empty response");

        try
        {
            var responseLogin = JsonSerializer.Deserialize<ResponseLogin>(content, JsonOpts);
            if (responseLogin is null || string.IsNullOrEmpty(responseLogin.Jwt))
                return (null, content);

            return (responseLogin.Jwt, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public (string uid, string? error) ExtractUidFromBearer(string jwt)
    {
        try
        {
            var uid = JwtHelper.ExtractUidFromToken(jwt).ToString();
            return (uid, null);
        }
        catch (Exception ex)
        {
            return ("", ex.Message);
        }
    }

    public async Task<(Dictionary<string, string>? courses, string? error)> GetCoursesAsync(
        string bearer, string uid, CancellationToken ct = default, int? pinnedTunnel = null)
    {
        var url = $"https://www.chessable.com/api/v1/getHomeData?uid={uid}&sortBookRowsBy=alphabetically&userLanguageShort=en";

        string content;
        try
        {
            content = await CurlGetAsync(url, bearer, "courses", uid, ct, pinnedTunnel, coupleToken: false);
        }
        catch (Exception ex)
        {
            return (null, $"curl failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(content) || content == "{}")
            return (null, "Empty response from Chessable");

        // Chessable antwortet bei abgelaufenem/ungültigem Bearer mit HTTP 200, aber einem
        // Fehler-Body {"error":{"message":"Expired token"}}. Ohne diese Erkennung würde der Body
        // als (leere) Kursliste durchgehen → der User sähe „keine Kurse" statt der echten Ursache.
        if (TryGetChessableErrorMessage(content) is { } apiError)
            return (null, apiError);

        // Bei abgelaufenem Bearer / Cloudflare-Block / Proxy-Gateway-Fehler kommt statt JSON
        // eine HTML-Seite zurück (beginnt mit '<'). JsonSerializer.Deserialize würde damit mit
        // „'<' is an invalid start of a value" crashen und genau dieser kryptische Parser-Text
        // wurde als Fehler bis in die rookhub-UI durchgereicht (sah aus wie ein „Syntaxfehler").
        // Klassifizieren: Token abgelaufen vs. (vermutlich VPN-IP) durch Cloudflare blockiert.
        if (LooksLikeHtml(content))
            return (null, ClassifyBlockedResponse(content, bearer));

        try
        {
            var response = JsonSerializer.Deserialize<ResponseChapterList>(content, JsonOpts)
                ?? new ResponseChapterList();
            var courses = new Dictionary<string, string>();
            // Indexer statt Add: liefert Chessable doppelte bids (oder mehrere unbefüllte mit Bid==0),
            // würde Dictionary.Add mit „same key already added" werfen und die ganze Kursliste mit einer
            // kryptischen Meldung scheitern lassen. Idempotent: letzter Eintrag gewinnt.
            foreach (var book in response.HomeData.BooksList)
                courses[book.Bid.ToString()] = book.Name;
            return (courses, null);
        }
        catch (JsonException)
        {
            // Defensive Auffanglinie: irgendein anderer nicht-erwarteter Body (kein JSON,
            // keine HTML-Seite). Kryptischen Parser-Text NICHT weiterreichen.
            return (null, ClassifyBlockedResponse(content, bearer));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Leichte Vorab-Schätzung: nur die Kurs-Struktur (getCourse?includeVariations, EIN Request, KEINE
    /// per-Linie-getGame-Abrufe) holen und die Varianten je Kapitel summieren → Gesamt-Linienzahl.
    /// Für die „~N Linien · ~M min"-Anzeige in der Admin-Kursliste, bevor man importiert.
    /// </summary>
    public async Task<(int? totalLines, string? error)> GetCourseLineCountAsync(string bearer, string uid, string bid, CancellationToken ct = default)
    {
        using var _tag = LogContext.PushProperty("LogTags", "chessable,scrape");
        var url = $"https://www.chessable.com/api/v1/getCourse?uid={uid}&bid={bid}&includeVariations=true";
        string content = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { content = await CurlGetAsync(url, bearer, "course", uid, ct, coupleToken: false); }
            catch (Exception ex)
            {
                if (attempt == 0) { await Task.Delay(ProxyRetryDelayMs, ct); continue; }
                return (null, $"Failed to fetch course: {ex.Message}");
            }
            if (!string.IsNullOrWhiteSpace(content) && content != "{}") break;
            if (attempt == 0) await Task.Delay(ProxyRetryDelayMs, ct);
        }
        if (string.IsNullOrWhiteSpace(content) || content == "{}") return (null, "Empty course response");
        if (TryGetChessableErrorMessage(content) is { } apiError) return (null, apiError);
        if (LooksLikeHtml(content)) return (null, ClassifyBlockedResponse(content, bearer));
        try
        {
            var course = JsonSerializer.Deserialize<ResponseCourse>(content, JsonOpts);
            if (course?.Course?.Data is null || course.Course.Data.Count == 0) return (null, "Course has no chapters");
            var total = course.Course.Data.Sum(c => c.Variations.Count > 0 ? c.Variations.Count : c.Total);
            return (total, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>
    /// Diagnose: holt GENAU eine Linie (getGame für eine oid) über den echten Abruf-Pfad
    /// (curl-impersonate + VPN-Tunnel-Lease + Headers). Liefert Timing + ob die Antwort vollständig
    /// (nicht leer/{}/Block) ist + einen Body-Anfang. Wirft nicht — Fehler kommen im error-Feld.
    /// </summary>
    public async Task<(bool ok, int bytes, long ms, string? error, string snippet)> DebugFetchLineAsync(
        string bearer, string uid, int oid, CancellationToken ct = default)
    {
        var url = $"https://www.chessable.com/api/v1/getGame?lng=en&uid={uid}&oid={oid}";
        var sw = Stopwatch.StartNew();
        try
        {
            var content = await CurlGetAsync(url, bearer, "line", uid, ct, coupleToken: false);
            sw.Stop();
            var ok = RawLineCache.IsComplete(content);
            var snippet = content is null ? "" : content[..Math.Min(160, content.Length)];
            return (ok, content?.Length ?? 0, sw.ElapsedMilliseconds, null, snippet);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, 0, sw.ElapsedMilliseconds, ex.Message, "");
        }
    }

    public async Task<(RestResponseCourse? data, string? error)> FetchCourseDataAsync(
        string bearer, string uid, string bid,
        Action<string>? onChapterProgress = null,
        Action<string>? onLineProgress = null,
        Action<string>? onCumulativeLines = null,
        Action<string>? onRetry = null,
        Action<int>? onTotalLines = null,
        bool bypassLineCache = false,
        CancellationToken ct = default)
    {
        // Alle Lifecycle-Logs dieses Scrapes (Retries/Warnungen/Per-Request) für die
        // zentrale Kibana-Filterung mit dem Domänen-Tag versehen → ECS `tags`.
        using var _tagScope = LogContext.PushProperty("LogTags", "chessable,scrape");

        // 1. Fetch course structure — der gluetun-Proxy liefert direkt nach einer
        //    VPN-Rotation kurz 503 (CONNECT tunnel failed). Anders als der Line-Fetch
        //    hatte dieser Aufruf bisher keinen Retry → ein einziges 503 ließ den
        //    ganzen Import mit "Empty course response" scheitern. Daher bounded Retry.
        // includeVariations=true → die getCourse-Antwort enthält je Kapitel die Varianten(-Liste) inkl.
        // Anzahl → wir kennen die Gesamt-Linienzahl SOFORT (vor den teuren getGame-Abrufen) für
        // Fortschritt/ETA. Ändert die gecachte Kurs-Rohdaten (größer); der Parser ignoriert Zusatzfelder.
        var courseUrl = $"https://www.chessable.com/api/v1/getCourse?uid={uid}&bid={bid}&includeVariations=true";
        string courseContent = "";
        for (int attempt = 0; attempt < CourseFetchAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                courseContent = await CurlGetAsync(courseUrl, bearer, "course", uid, ct);
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
                return (null, $"Failed to fetch course (proxy tunnel unavailable): {ex.Message}");
            }
            catch (Exception ex)
            {
                return (null, $"Failed to fetch course: {ex.Message}");
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

        if (string.IsNullOrWhiteSpace(courseContent) || courseContent == "{}")
            return (null, "Empty course response");

        // Abgelaufener Bearer-Fehler-Body bzw. HTML-Seite (Token/Cloudflare/Proxy) → sprechende
        // Meldung statt „Failed to parse course JSON" bzw. dem rohen JSON-Parser-Text; dabei
        // Token-Ablauf von (vermutlich VPN-IP-)Block unterscheiden.
        if (TryGetChessableErrorMessage(courseContent) is { } courseApiError)
            return (null, courseApiError);
        if (LooksLikeHtml(courseContent))
            return (null, ClassifyBlockedResponse(courseContent, bearer));

        ResponseCourse? course;
        try
        {
            course = JsonSerializer.Deserialize<ResponseCourse>(courseContent, JsonOpts);
        }
        catch
        {
            return (null, "Failed to parse course JSON");
        }

        if (course?.Course?.Data is null || course.Course.Data.Count == 0)
            return (null, "Course has no chapters");

        // Gesamt-Linienzahl direkt aus der getCourse-Antwort (includeVariations) melden — bekannt
        // VOR den teuren per-Linie-getGame-Abrufen. Summe der Varianten je Kapitel (Fallback: "total").
        var totalLines = course.Course.Data.Sum(c => c.Variations.Count > 0 ? c.Variations.Count : c.Total);
        if (totalLines > 0) onTotalLines?.Invoke(totalLines);

        var restResponseCourse = new RestResponseCourse
        {
            CourseJsonContent = courseContent
        };

        int cumLines = 0;

        // 2. Fetch each chapter
        for (int chapterIdx = 0; chapterIdx < course.Course.Data.Count; chapterIdx++)
        {
            ct.ThrowIfCancellationRequested();

            var chapter = course.Course.Data[chapterIdx];
            onChapterProgress?.Invoke($"{chapterIdx + 1} / {course.Course.Data.Count}");

            // Kapitel-Struktur (getList) mit Validierung + Retry holen: nur ein Body, der die gemeinsame
            // Prüfregel besteht (ChessableContent.ChapterReason), wird akzeptiert. Ein leerer/abgeschnittener
            // Body wird im selben Lauf erneut vom Server geholt, statt ihn truncated weiterzuverarbeiten und
            // zu cachen (sonst Kurs lückenhaft bzw. Crash beim Replay). Ein Chessable-Fehlerkörper (HTTP 200)
            // ist ein Fehlversuch; betrifft er das Token, bricht der Abruf sofort ab, sonst nach dem letzten
            // Versuch — nie wird er als Kapitel ohne Linien weitergereicht.
            var chapterUrl = $"https://www.chessable.com/api/v1/getList?uid={uid}&bid={bid}&lid={chapter.Id}";
            string chapterContent = "";
            ResponseChapter? responseChapter = null;
            for (int attempt = 0; attempt < ChapterFetchAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    chapterContent = await CurlGetAsync(chapterUrl, bearer, "chapter", uid, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch chapter {ChapterId}, attempt {Attempt}", chapter.Id, attempt + 1);
                    chapterContent = "";
                }

                responseChapter = TryParseChapter(chapterContent);
                if (responseChapter is not null)
                    break; // vollständig geparst → ok

                var chapterApiError = TryGetChessableErrorMessage(chapterContent ?? "", out var chapterTokenError);
                if (chapterTokenError)
                    return AbortFetch(bid, chapterApiError!);

                if (attempt < ChapterFetchAttempts - 1)
                {
                    _logger.LogWarning(
                        "Chapter {ChapterId} empty/truncated (len {Len}), retry {Attempt}/{Total}",
                        chapter.Id, chapterContent?.Length ?? 0, attempt + 1, ChapterFetchAttempts);
                    onRetry?.Invoke($"Kapitel {chapterIdx + 1}: unvollständig, Retry {attempt + 1}/{ChapterFetchAttempts} ...");
                    await Task.Delay(ProxyRetryDelayMs, ct);
                }
                else
                {
                    if (chapterApiError is not null)
                        return AbortFetch(bid, $"{chapterApiError} (Kapitel {chapter.Id}, nach {ChapterFetchAttempts} Versuchen)");
                    _logger.LogWarning(
                        "Chapter {ChapterId} still empty/truncated after {Total} attempts — skipping (course won't be cached)",
                        chapter.Id, ChapterFetchAttempts);
                    responseChapter = new ResponseChapter();
                }
            }
            responseChapter ??= new ResponseChapter();

            var restResponseChapter = new RestResponseChapter
            {
                ChapterJsonContent = chapterContent
            };

            // 3. Fetch each line in the chapter — reihenfolge-erhaltend, optional parallel
            //    (Chessable:ParallelLineFetches). Die VPN-Rotation ist drain-aware, wechselt die
            //    IP also nie mitten in einem laufenden Request — daher ist Parallelität block-sicher.
            var lines = responseChapter.List.Data;
            var lineSlots = new RestResponseLine[lines.Count];
            int chapterDone = 0;
            // Erster Grund, den ganzen Abruf abzubrechen (Chessable-Fehlerkörper bei einer Linie). Parallele
            // Linien sehen ihn und hören auf; das Kapitel wird danach nicht weiterverarbeitet.
            string? abortError = null;

            async Task FetchLineAsync(int lineIdx)
            {
                if (Volatile.Read(ref abortError) is not null) return;
                var line = lines[lineIdx];
                var lineUrl = $"https://www.chessable.com/api/v1/getGame?lng=en&uid={uid}&oid={line.Id}";
                string round = $"{(chapterIdx + 2):000}.{(lineIdx + 2):000}";

                // Resume-Cache: eine schon einmal erfolgreich geholte Linie (oid) wiederverwenden →
                // kein Chessable-Call, keine Inter-Request-Pause. Beim Force-Refresh übergangen —
                // die Linie wird nach erfolgreichem Abruf per Upsert ersetzt. Bewusst UMGEHEN statt
                // vorher löschen: scheitert der Abruf (Block, totes Bearer), bleibt der alte,
                // funktionierende Stand erhalten statt unwiederbringlich weg zu sein.
                string? lineContent = bypassLineCache ? null : await _lineCache.GetAsync(line.Id, bid, ct);
                bool fromCache = lineContent is not null;

                if (!fromCache)
                {
                    lineContent = "";
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            lineContent = await CurlGetAsync(lineUrl, bearer, "line", uid, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "curl failed for line {LineId}, attempt {Attempt}", line.Id, attempt + 1);
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
                            Interlocked.CompareExchange(ref abortError,
                                lineTokenError ? lineApiError : $"{lineApiError} (Linie {line.Id}, nach 10 Versuchen)", null);
                            return;
                        }
                        if (Volatile.Read(ref abortError) is not null) return;

                        if (attempt < 9)
                        {
                            // Der Block hat die IP bereits retired (ReportBlocked in CurlGetAsync) → der
                            // nächste CurlGetAsync läuft auf einem frischen Tunnel. Daher nur kurzer
                            // Backoff statt der früheren 30 s (größter Zeitfresser des Imports).
                            onRetry?.Invoke($"[{round}] Retry {attempt + 1}/10 ...");
                            await Task.Delay(_blockRetryDelayMs + Random.Shared.Next(0, 500), ct);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Chessable line fetch gave up after 10 attempts, skipping line {LineId} (round {Round})",
                                line.Id, round);
                            onRetry?.Invoke($"[{round}] FAILED after 10 attempts, skipping.");
                        }
                    }

                    // Nur vollständig parsbare Linien cachen → kein vergifteter Resume-Cache.
                    if (LineParses(lineContent))
                        await _lineCache.SetAsync(line.Id, lineContent, bid, ct);
                }

                lineSlots[lineIdx] = new RestResponseLine { Oid = line.Id, LineJsonContent = lineContent ?? "" };

                onCumulativeLines?.Invoke(Interlocked.Increment(ref cumLines).ToString());
                onLineProgress?.Invoke($"{Interlocked.Increment(ref chapterDone)} / {lines.Count}");

                // Inter-Request-Pause nur nach echtem Request (Cache-Treffer braucht keine).
                if (!fromCache) await RandomDelayAsync(ct);
            }

            if (_parallelLineFetches <= 1)
            {
                for (int lineIdx = 0; lineIdx < lines.Count && abortError is null; lineIdx++)
                {
                    ct.ThrowIfCancellationRequested();
                    await FetchLineAsync(lineIdx);
                }
            }
            else
            {
                using var sem = new SemaphoreSlim(_parallelLineFetches);
                var tasks = new List<Task>(lines.Count);
                for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
                {
                    ct.ThrowIfCancellationRequested();
                    await sem.WaitAsync(ct);
                    if (Volatile.Read(ref abortError) is not null) { sem.Release(); break; }
                    int idx = lineIdx;
                    tasks.Add(Task.Run(async () =>
                    {
                        try { await FetchLineAsync(idx); }
                        finally { sem.Release(); }
                    }, ct));
                }
                await Task.WhenAll(tasks);
            }

            if (abortError is not null)
                return AbortFetch(bid, abortError);

            // In Original-Reihenfolge anhängen (Parallelität ändert die Reihenfolge nicht).
            foreach (var slot in lineSlots)
                restResponseChapter.ResponseLineList.Add(slot ?? new RestResponseLine { LineJsonContent = "" });

            restResponseCourse.ChapterList.Add(restResponseChapter);

            // Random delay between chapter requests
            if (chapterIdx < course.Course.Data.Count - 1)
                await RandomDelayAsync(ct);
        }

        return (restResponseCourse, null);
    }

    /// <summary>Bricht den Kursabruf mit einer Chessable-Fehlermeldung ab (kein Teil-Kurs, nichts wird gecacht).</summary>
    private (RestResponseCourse? data, string? error) AbortFetch(string bid, string error)
    {
        _logger.LogWarning("Kursabruf bid {Bid} abgebrochen: {Error}", bid, error);
        return (null, error);
    }

    /// <param name="coupleToken">Ob dieser Request die token-gekoppelte IP-Rotation auslösen darf.
    /// Nur der tiefe Kurs-Abruf (Kurs/Kapitel/Linien) koppelt; leichte Einzel-Requests (Kursliste,
    /// Bearer-Test, Zeilen-Schätzung, Debug) laufen entkoppelt mit: sonst erzwingt ein einziger
    /// Fremd-Request mitten in einem Import zwei volle Rotationen (hin und zurück) und bremst ihn aus.</param>
    private async Task<string> CurlGetAsync(string url, string bearer, string endpoint, string? chessableUid, CancellationToken ct, int? pinnedTunnel = null, bool coupleToken = true)
    {
        // Tunnel leihen: wählt round-robin einen VPN-Tunnel, zählt/rotiert ihn (drain-aware → kein
        // IP-Wechsel mitten im Request, auch parallel) und liefert dessen Proxy. Dispose im finally
        // meldet den Request beim Tunnel als fertig. Ist ein Tunnel gepinnt (Test), läuft der Request
        // fix über GENAU diesen Tunnel statt über das round-robin.
        using var lease = pinnedTunnel is int pinIdx
            ? await _vpn.AcquireSpecificAsync(pinIdx, ct)
            : await _vpn.AcquireAsync(coupleToken ? chessableUid : null, ct);

        // Chessable-Username aus dem Bearer ziehen und für die Request-Logs in den
        // LogContext legen → erscheint als user.name (statt OS-User "root", siehe Program.cs).
        var uname = ChessableJwt.TryExtractUname(bearer);
        using IDisposable? userScope = uname is null ? null : LogContext.PushProperty("ChessableUser", uname);

        // Bearer über stdin statt als argv-Token (S2-006): die Argumentliste steht für jedes lokale Konto in
        // /proc/<pid>/cmdline, stdin nicht.
        var args = BuildGetArgs(url, _requestMaxTimeSec);
        var result = await RunCurlAsync(args, BuildGetHeaderStdin(bearer), url, endpoint, chessableUid, lease.ProxyUrl, ct);

        // IP-Soft-Block (leeres "{}"/leere Antwort trotz Transport-Erfolg): diese Ausgangs-IP ist
        // verbrannt → sofort retiren (Tunnel rotiert im Hintergrund, Pool wechselt auf den nächsten,
        // bereits ausgeruhten Tunnel). Der Aufrufer fällt dann nur kurz zurück und holt die Linie auf
        // der frischen IP — statt 30 s auf derselben heißen IP zu warten.
        // Nur im alten Modell (RotateOnBlock=true) retired ein Block die IP sofort. Im neuen
        // Token-gekoppelten Modell bleibt die IP sticky (kein Rotate-on-Block) — der langsame Takt
        // hält die Block-Rate niedrig, gewechselt wird nur bei Token-Wechsel.
        if (_rotateOnBlock && IsSoftBlockedBody(result))
            lease.ReportBlocked();

        return result;
    }

    private async Task<string> CurlPostAsync(string url, string body, string endpoint, string? chessableUid, CancellationToken ct)
    {
        using var lease = await _vpn.AcquireAsync(ct);
        var args = BuildPostArgs(url);
        return await RunCurlAsync(args, body, url, endpoint, chessableUid, lease.ProxyUrl, ct);
    }

    private async Task<string> RunCurlAsync(List<string> args, string? stdinBody, string url, string endpoint, string? chessableUid, string? proxyUrl, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string stdout = "";
        int exitCode = -1;
        string? error = null;
        bool transientProxyFailure = false;

        try
        {
            // Prozess (Proxy-Argument, stdin, beide Pipes, Kill bei Abbruch) steckt im ICurlRunner.
            var run = await _curl.RunAsync(args, stdinBody, proxyUrl, ct);
            stdout = run.Stdout;
            var stderr = run.Stderr;
            exitCode = run.ExitCode;

            if (exitCode != 0)
            {
                _logger.LogWarning(LogWatcherContract.CurlExited + " {Code}: {Stderr}", exitCode, stderr);
                error = stderr;
                transientProxyFailure = IsTransientProxyFailure(exitCode, stderr);
            }

            // Pro Request EINE Info-Zeile mit Outcome: Chessable liefert bei IP-basiertem
            // Soft-Block ein leeres "{}" (Länge ≤2) trotz Transport-Erfolg → als BLOCKED
            // markieren, damit Block-Rate + Timing direkt aus den Logs ablesbar sind.
            var blocked = stdout.Length <= 2;
            _logger.LogInformation("curl {Endpoint} → {Length}B {Outcome} ({Ms}ms)",
                endpoint, stdout.Length, blocked ? "BLOCKED(empty)" : "ok", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            await _audit.PersistAsync(endpoint, chessableUid, url, exitCode, stdout, (int)sw.ElapsedMilliseconds, error, ct);
        }

        // Nach dem Persistieren werfen, damit der Rohlog erhalten bleibt. Der Aufrufer
        // (Kurs-Struktur-Abruf) kann darauf gezielt einen Retry machen.
        if (transientProxyFailure)
            throw new ProxyTunnelException(error ?? "proxy tunnel failed (503)");

        return stdout;
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

    private static string ComputeSha512Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA512.HashData(bytes);
        var builder = new StringBuilder();
        foreach (var b in hashBytes)
            builder.Append(b.ToString("x2"));
        return builder.ToString();
    }
}
