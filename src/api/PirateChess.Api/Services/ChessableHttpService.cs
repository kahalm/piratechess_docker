using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Serilog.Context;
using piratechess_lib;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;

namespace PirateChess.Api.Services;

public class ChessableHttpService : IChessableHttpService
{
    private readonly ILogger<ChessableHttpService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVpnRotationService _vpn;
    // Hinweis: Der zu nutzende Proxy kommt jetzt pro Request aus dem VPN-Lease (Multi-Tunnel),
    // nicht mehr aus einem festen Feld.
    private readonly RawLineCache _lineCache;
    private readonly string _curlPath;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Der Kurs-Struktur-Abruf hatte bisher keinen Retry. Direkt nach einer VPN-
    // Rotation liefert der gluetun-Proxy kurz 503 (CONNECT tunnel failed) → ein
    // einziges 503 ließ den ganzen Import scheitern. Daher bounded Retry mit Pause,
    // bis der Tunnel wieder steht.
    private const int CourseFetchAttempts = 4;
    private const int ProxyRetryDelayMs = 4000;

    // curl bricht die (Proxy-)CONNECT-Phase nach so vielen Sekunden ab, statt im Default bis ~300 s
    // zu hängen. Bei soft-geblockter/hängender VPN-IP fror ein Request sonst den Import minutenlang
    // ein; jetzt schlägt er nach 30 s fehl → der Line-Retry (30 s Backoff) greift schnell.
    private const int CurlConnectTimeoutSec = 30;

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

    // TLS flags from curl_chrome116 wrapper — needed for Chrome TLS fingerprint
    private const string TlsFlags =
        "--ciphers TLS_AES_128_GCM_SHA256,TLS_AES_256_GCM_SHA384,TLS_CHACHA20_POLY1305_SHA256,"
        + "ECDHE-ECDSA-AES128-GCM-SHA256,ECDHE-RSA-AES128-GCM-SHA256,"
        + "ECDHE-ECDSA-AES256-GCM-SHA384,ECDHE-RSA-AES256-GCM-SHA384,"
        + "ECDHE-ECDSA-CHACHA20-POLY1305,ECDHE-RSA-CHACHA20-POLY1305,"
        + "ECDHE-RSA-AES128-SHA,ECDHE-RSA-AES256-SHA,"
        + "AES128-GCM-SHA256,AES256-GCM-SHA384,AES128-SHA,AES256-SHA"
        + " --http2 --http2-no-server-push --compressed"
        + " --tlsv1.2 --alps --tls-permute-extensions"
        + " --cert-compression brotli";

    // Dieselben TLS-Flags als einzelne argv-Tokens (kein Token enthält Leerzeichen → split by space).
    // Werden über ProcessStartInfo.ArgumentList übergeben, nicht als ein String → keine Arg-Injektion.
    private static readonly string[] TlsArgs = TlsFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public ChessableHttpService(
        ILogger<ChessableHttpService> logger,
        IServiceScopeFactory scopeFactory,
        IVpnRotationService vpn,
        RawLineCache lineCache,
        IConfiguration configuration)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _vpn = vpn;
        _lineCache = lineCache;

        // Use curl-impersonate-chrome binary directly (NOT the wrapper scripts
        // which add their own browser headers causing duplicates)
        _curlPath = "/usr/local/bin/curl-impersonate-chrome";

        // Speed-Stellschrauben (per ENV justierbar). Der Block ist requests-pro-IP-getrieben, NICHT
        // timing-getrieben (Prod-Messung) → der Inter-Request-Delay dient kaum der Block-Vermeidung;
        // Default daher klein gehalten (Block-Vermeidung läuft über die IP-Rotation).
        _delayMinMs = Math.Max(0, configuration.GetValue("Chessable:InterRequestDelayMinMs", 0));
        _delayMaxMs = Math.Max(_delayMinMs + 1, configuration.GetValue("Chessable:InterRequestDelayMaxMs", 200));
        _parallelLineFetches = Math.Clamp(configuration.GetValue("Chessable:ParallelLineFetches", 1), 1, 16);
        _blockRetryDelayMs = Math.Max(0, configuration.GetValue("Chessable:BlockRetryDelayMs", 1500));
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
        string bearer, string uid, CancellationToken ct = default)
    {
        var url = $"https://www.chessable.com/api/v1/getHomeData?uid={uid}&sortBookRowsBy=alphabetically&userLanguageShort=en";

        string content;
        try
        {
            content = await CurlGetAsync(url, bearer, "courses", uid, ct);
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
            foreach (var book in response.HomeData.BooksList)
                courses.Add(book.Bid.ToString(), book.Name);
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
            try { content = await CurlGetAsync(url, bearer, "course", uid, ct); }
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
            var content = await CurlGetAsync(url, bearer, "line", uid, ct);
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

    /// <summary>True, wenn der Body offensichtlich HTML/XML statt JSON ist (erstes
    /// Nicht-Whitespace-Zeichen ist <c>&lt;</c>) — typisch für Login-Redirects,
    /// Cloudflare-Block- oder Proxy-Gateway-Seiten.</summary>
    internal static bool LooksLikeHtml(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        foreach (var c in content)
        {
            if (char.IsWhiteSpace(c)) continue;
            return c == '<';
        }
        return false;
    }

    /// <summary>True, wenn der Body eine Cloudflare-Block-/Challenge-Seite ist
    /// (HTTP 403 „you have been blocked" / „Attention Required" / Ray-ID).</summary>
    internal static bool IsCloudflareBlockPage(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        return (content.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
                && (content.Contains("Ray ID", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("cf-ray", StringComparison.OrdinalIgnoreCase)))
            || content.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)
            || content.Contains("you have been blocked", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Übersetzt eine Nicht-JSON-/Block-Antwort in eine handlungsleitende Meldung und unterscheidet
    /// dabei die beiden grundverschiedenen Ursachen:
    /// <list type="bullet">
    /// <item><b>Token abgelaufen/ungültig</b> → der User muss einen neuen Bearer hinterlegen.</item>
    /// <item><b>Zugriff blockiert</b> (Cloudflare-403, Token aber NICHT lokal abgelaufen) → mit
    /// hoher Wahrscheinlichkeit die VPN-Ausgangs-IP gesperrt (z. B. M247) → IP rotieren/Server wechseln.</item>
    /// </list>
    /// Diskriminator ist der lokal aus dem JWT lesbare <c>exp</c>-Claim (<see cref="JwtHelper.IsExpired"/>):
    /// ein nicht abgelaufener Bearer + Cloudflare-Block deutet auf die IP, nicht auf den Token.
    /// </summary>
    internal static string ClassifyBlockedResponse(string content, string? bearer)
    {
        bool tokenExpired = !string.IsNullOrEmpty(bearer) && SafeIsExpired(bearer);
        if (tokenExpired)
            return "Chessable-Token ist abgelaufen — bitte den Bearer neu hinterlegen.";

        if (IsCloudflareBlockPage(content))
            return "Zugriff von Chessable/Cloudflare blockiert (HTTP 403). Der Token ist nicht abgelaufen → " +
                   "sehr wahrscheinlich ist die VPN-Ausgangs-IP gesperrt: IP rotieren bzw. VPN-Server wechseln. " +
                   "Falls das nicht hilft, den Bearer prüfen.";

        // HTML/sonstige Nicht-JSON-Antwort ohne eindeutige Cloudflare-Marker.
        return "Chessable lieferte kein gültiges JSON (Token ungültig oder Zugriff blockiert) — " +
               "bitte den Bearer neu hinterlegen bzw. die VPN-IP prüfen.";
    }

    private static bool SafeIsExpired(string bearer)
    {
        try { return JwtHelper.IsExpired(bearer); }
        catch { return false; }
    }

    /// <summary>Erkennt einen Chessable-Fehler-Body (z. B. <c>{"error":{"message":"Expired token"}}</c>
    /// oder <c>{"error":"…"}</c>) und liefert eine sprechende Meldung; sonst <c>null</c>.</summary>
    public static string? TryGetChessableErrorMessage(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var err))
                return null;
            var message = err.ValueKind switch
            {
                JsonValueKind.String => err.GetString(),
                JsonValueKind.Object when err.TryGetProperty("message", out var m) => m.GetString(),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(message)) return null;
            // „Expired token" / „Invalid token" → eindeutiger Hinweis auf einen neuen Bearer.
            return message.Contains("token", StringComparison.OrdinalIgnoreCase)
                ? $"Chessable-Token abgelaufen/ungültig ({message}) — bitte den Bearer neu hinterlegen."
                : $"Chessable: {message}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<(RestResponseCourse? data, string? error)> FetchCourseDataAsync(
        string bearer, string uid, string bid,
        Action<string>? onChapterProgress = null,
        Action<string>? onLineProgress = null,
        Action<string>? onCumulativeLines = null,
        Action<string>? onRetry = null,
        Action<int>? onTotalLines = null,
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

            // Kapitel-Struktur (getList) mit Validierung + Retry holen: nur ein vollständig als
            // ResponseChapter parsbarer Body wird akzeptiert. Ein leerer/abgeschnittener Body wird
            // im selben Lauf erneut vom Server geholt, statt ihn truncated weiterzuverarbeiten und
            // zu cachen (sonst Kurs lückenhaft bzw. Crash beim Replay).
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

            async Task FetchLineAsync(int lineIdx)
            {
                var line = lines[lineIdx];
                var lineUrl = $"https://www.chessable.com/api/v1/getGame?lng=en&uid={uid}&oid={line.Id}";
                string round = $"{(chapterIdx + 2):000}.{(lineIdx + 2):000}";

                // Resume-Cache: eine schon einmal erfolgreich geholte Linie (oid) wiederverwenden →
                // kein Chessable-Call, keine Inter-Request-Pause.
                string? lineContent = await _lineCache.GetAsync(line.Id, ct);
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

                        // Nur einen vollständig als ResponseLine parsbaren Body akzeptieren — ein
                        // abgeschnittener (nicht-leerer) Body würde sonst als "Erfolg" durchgehen
                        // und den Cache/Export vergiften.
                        if (LineParses(lineContent))
                            break;

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
                        await _lineCache.SetAsync(line.Id, lineContent, ct);
                }

                lineSlots[lineIdx] = new RestResponseLine { Oid = line.Id, LineJsonContent = lineContent ?? "" };

                onCumulativeLines?.Invoke(Interlocked.Increment(ref cumLines).ToString());
                onLineProgress?.Invoke($"{Interlocked.Increment(ref chapterDone)} / {lines.Count}");

                // Inter-Request-Pause nur nach echtem Request (Cache-Treffer braucht keine).
                if (!fromCache) await RandomDelayAsync(ct);
            }

            if (_parallelLineFetches <= 1)
            {
                for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
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
                    int idx = lineIdx;
                    tasks.Add(Task.Run(async () =>
                    {
                        try { await FetchLineAsync(idx); }
                        finally { sem.Release(); }
                    }, ct));
                }
                await Task.WhenAll(tasks);
            }

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

    private async Task<string> CurlGetAsync(string url, string bearer, string endpoint, string? chessableUid, CancellationToken ct)
    {
        // Tunnel leihen: wählt round-robin einen VPN-Tunnel, zählt/rotiert ihn (drain-aware → kein
        // IP-Wechsel mitten im Request, auch parallel) und liefert dessen Proxy. Dispose im finally
        // meldet den Request beim Tunnel als fertig.
        using var lease = await _vpn.AcquireAsync(ct);

        // Chessable-Username aus dem Bearer ziehen und für die Request-Logs in den
        // LogContext legen → erscheint als user.name (statt OS-User "root", siehe Program.cs).
        var uname = ChessableJwt.TryExtractUname(bearer);
        using IDisposable? userScope = uname is null ? null : LogContext.PushProperty("ChessableUser", uname);

        var args = BuildGetArgs(url, bearer);
        var result = await RunCurlAsync(args, null, url, endpoint, chessableUid, lease.ProxyUrl, ct);

        // IP-Soft-Block (leeres "{}"/leere Antwort trotz Transport-Erfolg): diese Ausgangs-IP ist
        // verbrannt → sofort retiren (Tunnel rotiert im Hintergrund, Pool wechselt auf den nächsten,
        // bereits ausgeruhten Tunnel). Der Aufrufer fällt dann nur kurz zurück und holt die Linie auf
        // der frischen IP — statt 30 s auf derselben heißen IP zu warten.
        if (IsSoftBlockedBody(result))
            lease.ReportBlocked();

        return result;
    }

    /// <summary>True, wenn die Antwort ein IP-Soft-Block ist: leer oder nur <c>{}</c> (Länge ≤ 2)
    /// trotz erfolgreichem Transport — Chessables Signatur für eine request-rate-geblockte IP.</summary>
    internal static bool IsSoftBlockedBody(string? body) => string.IsNullOrEmpty(body) || body.Length <= 2;

    private async Task<string> CurlPostAsync(string url, string body, string endpoint, string? chessableUid, CancellationToken ct)
    {
        using var lease = await _vpn.AcquireAsync(ct);
        var args = BuildPostArgs(url);
        return await RunCurlAsync(args, body, url, endpoint, chessableUid, lease.ProxyUrl, ct);
    }

    private async Task<string> RunCurlAsync(List<string> args, string? stdinBody, string url, string endpoint, string? chessableUid, string? proxyUrl, CancellationToken ct)
    {
        _logger.LogDebug("curl: {Path} (proxy: {Proxy})", _curlPath, proxyUrl ?? "none");

        var psi = new ProcessStartInfo
        {
            FileName = _curlPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinBody is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // ArgumentList → jedes Token wird einzeln/escaped übergeben (keine Shell, keine Arg-Injektion).
        // curl-impersonate honoriert in unserem Setup keine HTTP(S)_PROXY-Env automatisch
        // → Proxy explizit als --proxy mitgeben, damit die Calls über gluetun/VPN laufen.
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            psi.ArgumentList.Add("--proxy");
            psi.ArgumentList.Add(proxyUrl);
        }
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var sw = Stopwatch.StartNew();
        string stdout = "";
        int exitCode = -1;
        string? error = null;
        bool transientProxyFailure = false;

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start {_curlPath}");

            // Bei Cancellation (z. B. Shutdown) den curl-Prozess aktiv beenden — Process.Dispose
            // killt ihn NICHT, sonst bliebe er als Waise hängen und WaitForExitAsync würde erst
            // mit seinem Ende zurückkehren.
            using var killReg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* Prozess bereits beendet / Race — egal */ }
            });

            if (stdinBody is not null)
            {
                await process.StandardInput.WriteAsync(stdinBody.AsMemory(), ct);
                process.StandardInput.Close();
            }

            stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);
            exitCode = process.ExitCode;

            if (exitCode != 0)
            {
                _logger.LogWarning("curl exited with code {Code}: {Stderr}", exitCode, stderr);
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
            await PersistRawResponseAsync(endpoint, chessableUid, url, exitCode, stdout, (int)sw.ElapsedMilliseconds, error, ct);
        }

        // Nach dem Persistieren werfen, damit der Rohlog erhalten bleibt. Der Aufrufer
        // (Kurs-Struktur-Abruf) kann darauf gezielt einen Retry machen.
        if (transientProxyFailure)
            throw new ProxyTunnelException(error ?? "proxy tunnel failed (503)");

        return stdout;
    }

    /// <summary>
    /// Erkennt einen transienten gluetun-Proxy-Ausfall: curl bricht mit „CONNECT
    /// tunnel failed, response 503" ab, während die VPN-IP rotiert/reconnectet.
    /// Solche Fehler sind nach wenigen Sekunden von selbst weg → ein Retry lohnt,
    /// im Gegensatz zu echten Fehlern (DNS, 401, …).
    /// </summary>
    /// <summary>
    /// Parst den getList-Body zu <see cref="ResponseChapter"/>. Liefert null bei leerem/<c>{}</c>-
    /// Body ODER bei abgeschnittenem/korruptem JSON (JsonException) → Signal zum Neu-Holen.
    /// Ein legitim leeres Kapitel (<c>{"list":{"data":[]}}</c>) parst dagegen und gilt als gültig.
    /// </summary>
    private static ResponseChapter? TryParseChapter(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content == "{}")
            return null;
        try
        {
            return JsonSerializer.Deserialize<ResponseChapter>(content, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True, wenn der getGame-Body vollständig als <see cref="ResponseLine"/> parst.</summary>
    private static bool LineParses(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content == "{}")
            return false;
        try
        {
            return JsonSerializer.Deserialize<ResponseLine>(content, JsonOpts) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsTransientProxyFailure(int exitCode, string? error)
    {
        if (string.IsNullOrEmpty(error))
            return false;

        return error.Contains("CONNECT tunnel failed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("response 503", StringComparison.OrdinalIgnoreCase)
            || error.Contains("HTTP code 503 from proxy", StringComparison.OrdinalIgnoreCase);
    }

    private async Task PersistRawResponseAsync(string endpoint, string? chessableUid, string url,
        int statusCode, string body, int durationMs, string? errorMessage, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChessableRawResponses.Add(new ChessableRawResponse
            {
                Endpoint = endpoint,
                ChessableUid = chessableUid,
                Url = url.Length > 500 ? url[..500] : url,
                StatusCode = statusCode,
                // gzip+Base64: die Roh-Bodies (Linien Ø ~210 KB, Kapitel Ø ~500 KB) waren bisher
                // unkomprimiert der mit Abstand größte Tabellen-Anteil. Niemand liest RawJson im Code
                // (reines Audit/Debug) → Kompression ist verhaltensneutral, ~3× kleiner.
                // Login-Antworten enthalten ein frisches Chessable-JWT → vor dem Speichern redigieren.
                RawJson = GzipText.Compress(RedactForStorage(endpoint, body ?? string.Empty)),
                DurationMs = durationMs,
                ErrorMessage = errorMessage,
                RequestedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Logging-Persistenz darf den eigentlichen Call nicht killen.
            _logger.LogWarning(ex, "Failed to persist ChessableRawResponse for {Endpoint}", endpoint);
        }
    }

    /// <summary>Redigiert sensible Werte aus einem Roh-Body vor dem Audit-Speichern. Aktuell: das
    /// <c>jwt</c>-Feld der Login-Antwort (frisches Chessable-Token) → <c>[redacted]</c>. Andere
    /// Endpunkte bleiben unverändert (reine Kurs-/Linien-Daten, kein Geheimnis).</summary>
    internal static string RedactForStorage(string endpoint, string body)
    {
        if (endpoint != "login" || string.IsNullOrEmpty(body)) return body;
        // "jwt":"<token>" → "jwt":"[redacted]" (tolerant ggü. Whitespace; Token enthält keine ").
        return Regex.Replace(body, "(\"jwt\"\\s*:\\s*\")[^\"]*(\")", "$1[redacted]$2");
    }

    private static void AddHeader(List<string> args, string header)
    {
        args.Add("-H");
        args.Add(header);
    }

    /// <summary>
    /// Baut die curl-Argumente als EINZELNE argv-Tokens (für <see cref="ProcessStartInfo.ArgumentList"/>).
    /// Jeder Wert — inkl. URL (mit user-naher bid) und Bearer — ist ein eigenständiges Argument; .NET
    /// escaped sie pro-Token. Dadurch kann KEIN Eingabewert curl-Flags injizieren (vorher floss die URL
    /// als <c>"{url}"</c> in einen Args-String → ein <c>"</c> in der bid konnte z.B. <c>-o</c>/<c>--config</c>
    /// einschleusen und Dateien lesen/schreiben).
    /// </summary>
    public static List<string> BuildGetArgs(string url, string bearer)
    {
        var args = new List<string> { "-s", "-S", "--connect-timeout", CurlConnectTimeoutSec.ToString() };
        args.AddRange(TlsArgs);
        AddHeader(args, "user-agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:138.0) Gecko/20100101 Firefox/138.0");
        AddHeader(args, "accept: application/json, text/plain, */*");
        AddHeader(args, "accept-language: en");
        AddHeader(args, "platform: Web");
        AddHeader(args, "x-os-name: Firefox");
        AddHeader(args, "x-os-version: 138");
        AddHeader(args, "x-device-model: Windows");
        AddHeader(args, $"authorization: Bearer {bearer}");
        AddHeader(args, "alt-used: www.chessable.com");
        AddHeader(args, "connection: keep-alive");
        AddHeader(args, "sec-fetch-dest: empty");
        AddHeader(args, "sec-fetch-mode: cors");
        AddHeader(args, "sec-fetch-site: same-origin");
        AddHeader(args, "priority: u=0");
        AddHeader(args, "te: trailers");
        AddHeader(args, "pragma: no-cache");
        AddHeader(args, "cache-control: no-cache");
        args.Add(url);
        return args;
    }

    /// <summary>Wie <see cref="BuildGetArgs"/>, für POST (Body via stdin, <c>-d @-</c>).</summary>
    public static List<string> BuildPostArgs(string url)
    {
        var args = new List<string> { "-s", "-S", "--connect-timeout", CurlConnectTimeoutSec.ToString() };
        args.AddRange(TlsArgs);
        args.Add("-X"); args.Add("POST");
        AddHeader(args, "user-agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:137.0) Gecko/20100101 Firefox/137.0");
        AddHeader(args, "accept: application/json, text/plain, */*");
        AddHeader(args, "accept-language: en");
        AddHeader(args, "referer: https://www.chessable.com/login/");
        AddHeader(args, "content-type: application/json;charset=utf-8");
        AddHeader(args, "platform: Web");
        AddHeader(args, "x-os-name: Firefox");
        AddHeader(args, "x-os-version: 137");
        AddHeader(args, "x-device-model: Windows");
        AddHeader(args, "origin: https://www.chessable.com");
        AddHeader(args, "alt-used: www.chessable.com");
        AddHeader(args, "connection: keep-alive");
        AddHeader(args, "sec-fetch-dest: empty");
        AddHeader(args, "sec-fetch-mode: cors");
        AddHeader(args, "sec-fetch-site: same-origin");
        AddHeader(args, "dnt: 1");
        AddHeader(args, "sec-gpc: 1");
        AddHeader(args, "priority: u=0");
        args.Add("-d"); args.Add("@-"); // read body from stdin
        args.Add(url);
        return args;
    }

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
