using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly RawLineCache _lineCache;
    private readonly string _curlPath;
    private readonly string? _proxyUrl;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Der Kurs-Struktur-Abruf hatte bisher keinen Retry. Direkt nach einer VPN-
    // Rotation liefert der gluetun-Proxy kurz 503 (CONNECT tunnel failed) → ein
    // einziges 503 ließ den ganzen Import scheitern. Daher bounded Retry mit Pause,
    // bis der Tunnel wieder steht.
    private const int CourseFetchAttempts = 4;
    private const int ProxyRetryDelayMs = 4000;

    // Der Kapitel-Abruf (getList) hatte bisher weder Validierung noch Retry: ein mitten im
    // Stream abgebrochener Body (~8 KB-Truncation durch den VPN-Proxy) ist nicht-leer, aber
    // unvollständig → er parst NICHT als ResponseChapter, wurde aber truncated gecacht und ließ
    // beim Replay (lib.GetCourse) JsonSerializer crashen → der ganze Kurs-Import scheiterte.
    // Daher abgeschnittene Kapitel sofort im selben Lauf neu vom Server holen.
    private const int ChapterFetchAttempts = 4;

    // Zufälliger Abstand zwischen zwei aufeinanderfolgenden Zeilen-/Kapitel-Requests
    // (menschenähnliches Timing). Prod-Messung 2026-06-15: Chessables Block ist NICHT
    // timing-getrieben sondern requests-pro-IP-getrieben (IP wird nach ~10 Requests
    // soft-geblockt, daher steuert Vpn:RotateAfterRequests=10 die Block-Rate ~0%).
    // Delay 2–5s brachte gg. Block nichts → zurück auf 1–2s, spart Kursdauer.
    private const int InterRequestDelayMinMs = 1000;
    private const int InterRequestDelayMaxMs = 2000;

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

        // Alle Chessable-Calls über den (VPN-)Proxy schicken, falls konfiguriert
        // (Chessable:ProxyUrl = http://gluetun:8888) — sonst gehen sie mit der Host-IP
        // raus und profitieren NICHT von der gluetun-IP-Rotation.
        _proxyUrl = configuration["Chessable:ProxyUrl"];
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

        try
        {
            var response = JsonSerializer.Deserialize<ResponseChapterList>(content, JsonOpts)
                ?? new ResponseChapterList();
            var courses = new Dictionary<string, string>();
            foreach (var book in response.HomeData.BooksList)
                courses.Add(book.Bid.ToString(), book.Name);
            return (courses, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public async Task<(RestResponseCourse? data, string? error)> FetchCourseDataAsync(
        string bearer, string uid, string bid,
        Action<string>? onChapterProgress = null,
        Action<string>? onLineProgress = null,
        Action<string>? onCumulativeLines = null,
        Action<string>? onRetry = null,
        CancellationToken ct = default)
    {
        // 1. Fetch course structure — der gluetun-Proxy liefert direkt nach einer
        //    VPN-Rotation kurz 503 (CONNECT tunnel failed). Anders als der Line-Fetch
        //    hatte dieser Aufruf bisher keinen Retry → ein einziges 503 ließ den
        //    ganzen Import mit "Empty course response" scheitern. Daher bounded Retry.
        var courseUrl = $"https://www.chessable.com/api/v1/getCourse?uid={uid}&bid={bid}";
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

            // 3. Fetch each line in the chapter
            for (int lineIdx = 0; lineIdx < responseChapter.List.Data.Count; lineIdx++)
            {
                ct.ThrowIfCancellationRequested();

                var line = responseChapter.List.Data[lineIdx];
                onLineProgress?.Invoke($"{lineIdx + 1} / {responseChapter.List.Data.Count}");

                var lineUrl = $"https://www.chessable.com/api/v1/getGame?lng=en&uid={uid}&oid={line.Id}";
                string round = $"{(chapterIdx + 2):000}.{(lineIdx + 2):000}";

                // Resume-Cache: eine schon einmal erfolgreich geholte Linie (oid) wiederverwenden →
                // kein Chessable-Call, keine Inter-Request-Pause. Bricht ein Kursabruf in der Mitte
                // ab, holt der Neustart so nur noch die fehlenden Linien.
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
                            onRetry?.Invoke($"[{round}] Retry {attempt + 1}/10 ...");
                            await Task.Delay(30000 + Random.Shared.Next(0, 5000), ct);
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

                restResponseChapter.ResponseLineList.Add(new RestResponseLine
                {
                    LineJsonContent = lineContent ?? ""
                });

                cumLines++;
                onCumulativeLines?.Invoke(cumLines.ToString());

                // Random delay between line requests — nur nach echtem Request (Cache-Treffer braucht keine).
                if (!fromCache && lineIdx < responseChapter.List.Data.Count - 1)
                    await Task.Delay(Random.Shared.Next(InterRequestDelayMinMs, InterRequestDelayMaxMs), ct);
            }

            restResponseCourse.ChapterList.Add(restResponseChapter);

            // Random delay between chapter requests
            if (chapterIdx < course.Course.Data.Count - 1)
                await Task.Delay(Random.Shared.Next(InterRequestDelayMinMs, InterRequestDelayMaxMs), ct);
        }

        return (restResponseCourse, null);
    }

    private async Task<string> CurlGetAsync(string url, string bearer, string endpoint, string? chessableUid, CancellationToken ct)
    {
        // Vor jedem Request prüfen, ob die VPN-IP rotiert werden soll (alle N Requests).
        // Sequentieller, awaited Loop → die Rotation liegt garantiert ZWISCHEN zwei
        // Requests, nie mitten in einem.
        await _vpn.MaybeRotateAsync(ct);

        // Chessable-Username aus dem Bearer ziehen und für die Request-Logs in den
        // LogContext legen → erscheint als user.name (statt OS-User "root", siehe Program.cs).
        var uname = ChessableJwt.TryExtractUname(bearer);
        using IDisposable? userScope = uname is null ? null : LogContext.PushProperty("ChessableUser", uname);

        var args = BuildGetArgs(url, bearer);
        return await RunCurlAsync(args, null, url, endpoint, chessableUid, ct);
    }

    private async Task<string> CurlPostAsync(string url, string body, string endpoint, string? chessableUid, CancellationToken ct)
    {
        var args = BuildPostArgs(url);
        return await RunCurlAsync(args, body, url, endpoint, chessableUid, ct);
    }

    private async Task<string> RunCurlAsync(List<string> args, string? stdinBody, string url, string endpoint, string? chessableUid, CancellationToken ct)
    {
        _logger.LogDebug("curl: {Path} (proxy: {Proxy})", _curlPath, _proxyUrl ?? "none");

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
        if (!string.IsNullOrEmpty(_proxyUrl))
        {
            psi.ArgumentList.Add("--proxy");
            psi.ArgumentList.Add(_proxyUrl);
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
                RawJson = body ?? string.Empty,
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
        var args = new List<string> { "-s", "-S" };
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
        var args = new List<string> { "-s", "-S" };
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
