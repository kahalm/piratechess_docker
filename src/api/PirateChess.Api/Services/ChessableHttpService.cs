using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog.Context;
using piratechess_lib;
using static PirateChess.Api.Services.ChessableResponseClassifier;

namespace PirateChess.Api.Services;

/// <summary>
/// Fassade des Chessable-Abrufs: Login, Kursliste, Zeilen-Schätzung, Diagnose und der tiefe Kursabruf
/// (<see cref="ChessableCourseFetcher"/>). Jeder Request läuft über <see cref="ChessableCurlTransport"/>
/// (VPN-Lease, curl, Audit); Klassifikation in <see cref="ChessableResponseClassifier"/>.
/// </summary>
public class ChessableHttpService : IChessableHttpService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly ChessableCurlTransport _transport;
    private readonly ChessableCourseFetcher _fetcher;

    /// <summary>Pause vor einem Retry nach Proxy-503/leerer Antwort (Kursstruktur, Kapitel, Zeilen-Schätzung).</summary>
    internal int ProxyRetryDelayMs   // Tests setzen ihn auf 0
    {
        get => _fetcher.ProxyRetryDelayMs;
        init => _fetcher.ProxyRetryDelayMs = value;
    }

    public ChessableHttpService(
        ILogger<ChessableHttpService> logger,
        RawResponseAudit audit,
        IVpnRotationService vpn,
        RawLineCache lineCache,
        ICurlRunner curl,
        IConfiguration configuration)
    {
        // Speed-Stellschrauben (per ENV justierbar). Der Block ist requests-pro-IP-getrieben, NICHT
        // timing-getrieben (Prod-Messung) → der Inter-Request-Delay dient kaum der Block-Vermeidung;
        // Default daher klein gehalten (Block-Vermeidung läuft über die IP-Rotation).
        // 2026-07-12: Default 10x hochgesetzt (200ms -> 2000ms) auf Zuruf, um den Crawl insgesamt
        // weniger aggressiv zu fahren (unabhaengig von der IP-Rotations-Logik oben).
        // Zufälliger Abstand zwischen zwei aufeinanderfolgenden Zeilen-/Kapitel-Requests
        // (menschenähnliches Timing). Prod-Messung 2026-06-15: Chessables Block ist NICHT
        // timing-getrieben sondern requests-pro-IP-getrieben (IP wird nach ~10 Requests
        // soft-geblockt, daher steuert Vpn:RotateAfterRequests die Block-Rate ~0%).
        // Konfigurierbar über Chessable:InterRequestDelayMinMs/MaxMs (zum Beschleunigen runtersetzen).
        var delayMinMs = Math.Max(0, configuration.GetValue("Chessable:InterRequestDelayMinMs", 0));
        var delayMaxMs = Math.Max(delayMinMs + 1, configuration.GetValue("Chessable:InterRequestDelayMaxMs", 2000));
        // Wie viele Zeilen eines Kapitels parallel geholt werden (Chessable:ParallelLineFetches).
        // 1 = sequenzielles Verhalten (Default). >1 beschleunigt; die VPN-Rotation ist drain-aware, wechselt
        // also die IP nie mitten in einem laufenden Request (siehe VpnRotationService).
        var parallelLineFetches = Math.Clamp(configuration.GetValue("Chessable:ParallelLineFetches", 1), 1, 16);
        // Backoff vor dem Wiederholungs-Fetch einer leer/{}-geblockten Linie. Früher fix 30 s — das war
        // mit Abstand der größte Zeitfresser (~6 % der Zeilen blocken → ~1,8 s/Zeile amortisiert). Jetzt
        // retired ein Block die IP SOFORT (lease.ReportBlocked → Tunnel rotiert + Pool wechselt), der
        // Retry läuft auf der frischen IP → es genügt ein kurzer Backoff. Konfigurierbar via
        // Chessable:BlockRetryDelayMs.
        var blockRetryDelayMs = Math.Max(0, configuration.GetValue("Chessable:BlockRetryDelayMs", 1500));
        // GESAMT-Timeout je Request (--max-time). Eine hängende IP lieferte den Body sonst erst nach
        // 75–120 s (curl 56), was die serielle Pipeline blockierte. Jetzt bricht der Request nach ~20 s
        // ab → leerer Body → als Soft-Block gewertet → IP retired/rotiert, Tunnel-Health zählt mit.
        // Gesunde Abrufe (auch große Linien/Kapitel) sind <5 s. Konfigurierbar: Chessable:RequestMaxTimeSec.
        var requestMaxTimeSec = Math.Clamp(configuration.GetValue("Chessable:RequestMaxTimeSec", 20), 5, 120);
        // Rotation bei IP-Soft-Block. Default true = bisheriges Verhalten (je Block sofort rotieren).
        // Prod fährt das neue Token-gekoppelte Modell: Vpn:RotateOnBlock=false + Vpn:RotateAfterRequests
        // sehr hoch + langsamer Takt → Rotation NUR bei Token-Wechsel (siehe VpnRotationService).
        var rotateOnBlock = configuration.GetValue("Vpn:RotateOnBlock", true);

        _transport = new ChessableCurlTransport(logger, audit, vpn, curl, requestMaxTimeSec, rotateOnBlock);
        _fetcher = new ChessableCourseFetcher(logger, _transport, lineCache,
            delayMinMs, delayMaxMs, parallelLineFetches, blockRetryDelayMs);
    }


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
            content = await _transport.PostAsync("https://www.chessable.com/api/v1/authenticate", json, "login", null, ct);
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
            content = await _transport.GetAsync(url, bearer, "courses", uid, ct, pinnedTunnel, coupleToken: false);
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
            try { content = await _transport.GetAsync(url, bearer, "course", uid, ct, coupleToken: false); }
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
            var content = await _transport.GetAsync(url, bearer, "line", uid, ct, coupleToken: false);
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

    public Task<(RestResponseCourse? data, string? error)> FetchCourseDataAsync(
        string bearer, string uid, string bid, CourseFetchOptions? options = null, CancellationToken ct = default)
        => _fetcher.FetchAsync(bearer, uid, bid, options ?? CourseFetchOptions.Default, ct);

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
