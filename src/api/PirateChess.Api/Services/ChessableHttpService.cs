using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using piratechess_lib;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;

namespace PirateChess.Api.Services;

public class ChessableHttpService : IChessableHttpService
{
    private readonly ILogger<ChessableHttpService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVpnRotationService _vpn;
    private readonly string _curlPath;
    private readonly string? _proxyUrl;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

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

    public ChessableHttpService(
        ILogger<ChessableHttpService> logger,
        IServiceScopeFactory scopeFactory,
        IVpnRotationService vpn,
        IConfiguration configuration)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _vpn = vpn;

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
        // 1. Fetch course structure
        var courseUrl = $"https://www.chessable.com/api/v1/getCourse?uid={uid}&bid={bid}";
        string courseContent;
        try
        {
            courseContent = await CurlGetAsync(courseUrl, bearer, "course", uid, ct);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to fetch course: {ex.Message}");
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

            var chapterUrl = $"https://www.chessable.com/api/v1/getList?uid={uid}&bid={bid}&lid={chapter.Id}";
            string chapterContent;
            try
            {
                chapterContent = await CurlGetAsync(chapterUrl, bearer, "chapter", uid, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch chapter {ChapterId}", chapter.Id);
                chapterContent = "{}";
            }

            var restResponseChapter = new RestResponseChapter
            {
                ChapterJsonContent = chapterContent
            };

            // Parse chapter to get lines
            ResponseChapter? responseChapter;
            try
            {
                responseChapter = JsonSerializer.Deserialize<ResponseChapter>(chapterContent, JsonOpts)
                    ?? new ResponseChapter();
            }
            catch
            {
                responseChapter = new ResponseChapter();
            }

            // 3. Fetch each line in the chapter
            for (int lineIdx = 0; lineIdx < responseChapter.List.Data.Count; lineIdx++)
            {
                ct.ThrowIfCancellationRequested();

                var line = responseChapter.List.Data[lineIdx];
                onLineProgress?.Invoke($"{lineIdx + 1} / {responseChapter.List.Data.Count}");

                var lineUrl = $"https://www.chessable.com/api/v1/getGame?lng=en&uid={uid}&oid={line.Id}";
                string lineContent = "";
                string round = $"{(chapterIdx + 2):000}.{(lineIdx + 2):000}";

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

                    if (!string.IsNullOrWhiteSpace(lineContent) && lineContent != "{}")
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

                restResponseChapter.ResponseLineList.Add(new RestResponseLine
                {
                    LineJsonContent = lineContent
                });

                cumLines++;
                onCumulativeLines?.Invoke(cumLines.ToString());

                // Random delay between line requests
                if (lineIdx < responseChapter.List.Data.Count - 1)
                    await Task.Delay(Random.Shared.Next(500, 1500), ct);
            }

            restResponseCourse.ChapterList.Add(restResponseChapter);

            // Random delay between chapter requests
            if (chapterIdx < course.Course.Data.Count - 1)
                await Task.Delay(Random.Shared.Next(500, 1500), ct);
        }

        return (restResponseCourse, null);
    }

    private async Task<string> CurlGetAsync(string url, string bearer, string endpoint, string? chessableUid, CancellationToken ct)
    {
        // Vor jedem Request prüfen, ob die VPN-IP rotiert werden soll (alle N Requests).
        // Sequentieller, awaited Loop → die Rotation liegt garantiert ZWISCHEN zwei
        // Requests, nie mitten in einem.
        await _vpn.MaybeRotateAsync(ct);

        var args = BuildGetArgs(url, bearer);
        return await RunCurlAsync(args, null, url, endpoint, chessableUid, ct);
    }

    private async Task<string> CurlPostAsync(string url, string body, string endpoint, string? chessableUid, CancellationToken ct)
    {
        var args = BuildPostArgs(url);
        return await RunCurlAsync(args, body, url, endpoint, chessableUid, ct);
    }

    private async Task<string> RunCurlAsync(string args, string? stdinBody, string url, string endpoint, string? chessableUid, CancellationToken ct)
    {
        // curl-impersonate honoriert in unserem Setup keine HTTP(S)_PROXY-Env automatisch
        // → Proxy explizit als --proxy mitgeben, damit die Calls über gluetun/VPN laufen.
        var finalArgs = string.IsNullOrEmpty(_proxyUrl) ? args : $"--proxy \"{_proxyUrl}\" {args}";
        _logger.LogInformation("curl: {Path} (proxy: {Proxy})", _curlPath, _proxyUrl ?? "none");

        var psi = new ProcessStartInfo
        {
            FileName = _curlPath,
            Arguments = finalArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinBody is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var sw = Stopwatch.StartNew();
        string stdout = "";
        int exitCode = -1;
        string? error = null;

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
            }

            _logger.LogInformation("curl response length: {Length}, preview: {Preview}",
                stdout.Length, stdout.Length > 100 ? stdout[..100] + "..." : stdout);
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

        return stdout;
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

    private static string BuildGetArgs(string url, string bearer)
    {
        var sb = new StringBuilder();
        sb.Append($"-s -S {TlsFlags}");
        sb.Append($" -H \"user-agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:138.0) Gecko/20100101 Firefox/138.0\"");
        sb.Append($" -H \"accept: application/json, text/plain, */*\"");
        sb.Append($" -H \"accept-language: en\"");
        sb.Append($" -H \"platform: Web\"");
        sb.Append($" -H \"x-os-name: Firefox\"");
        sb.Append($" -H \"x-os-version: 138\"");
        sb.Append($" -H \"x-device-model: Windows\"");
        sb.Append($" -H \"authorization: Bearer {bearer}\"");
        sb.Append($" -H \"alt-used: www.chessable.com\"");
        sb.Append($" -H \"connection: keep-alive\"");
        sb.Append($" -H \"sec-fetch-dest: empty\"");
        sb.Append($" -H \"sec-fetch-mode: cors\"");
        sb.Append($" -H \"sec-fetch-site: same-origin\"");
        sb.Append($" -H \"priority: u=0\"");
        sb.Append($" -H \"te: trailers\"");
        sb.Append($" -H \"pragma: no-cache\"");
        sb.Append($" -H \"cache-control: no-cache\"");
        sb.Append($" \"{url}\"");
        return sb.ToString();
    }

    private static string BuildPostArgs(string url)
    {
        var sb = new StringBuilder();
        sb.Append($"-s -S {TlsFlags} -X POST");
        sb.Append($" -H \"user-agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:137.0) Gecko/20100101 Firefox/137.0\"");
        sb.Append($" -H \"accept: application/json, text/plain, */*\"");
        sb.Append($" -H \"accept-language: en\"");
        sb.Append($" -H \"referer: https://www.chessable.com/login/\"");
        sb.Append($" -H \"content-type: application/json;charset=utf-8\"");
        sb.Append($" -H \"platform: Web\"");
        sb.Append($" -H \"x-os-name: Firefox\"");
        sb.Append($" -H \"x-os-version: 137\"");
        sb.Append($" -H \"x-device-model: Windows\"");
        sb.Append($" -H \"origin: https://www.chessable.com\"");
        sb.Append($" -H \"alt-used: www.chessable.com\"");
        sb.Append($" -H \"connection: keep-alive\"");
        sb.Append($" -H \"sec-fetch-dest: empty\"");
        sb.Append($" -H \"sec-fetch-mode: cors\"");
        sb.Append($" -H \"sec-fetch-site: same-origin\"");
        sb.Append($" -H \"dnt: 1\"");
        sb.Append($" -H \"sec-gpc: 1\"");
        sb.Append($" -H \"priority: u=0\"");
        sb.Append(" -d @-"); // read body from stdin
        sb.Append($" \"{url}\"");
        return sb.ToString();
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
