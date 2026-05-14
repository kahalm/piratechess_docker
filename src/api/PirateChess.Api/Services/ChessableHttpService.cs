using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using piratechess_lib;

namespace PirateChess.Api.Services;

public class ChessableHttpService : IChessableHttpService
{
    private readonly ILogger<ChessableHttpService> _logger;
    private readonly string _curlPath;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public ChessableHttpService(ILogger<ChessableHttpService> logger)
    {
        _logger = logger;

        // Prefer curl_chrome116 but fall back to curl_chrome if not found
        _curlPath = File.Exists("/usr/local/bin/curl_chrome116")
            ? "/usr/local/bin/curl_chrome116"
            : "/usr/local/bin/curl_chrome110";
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
            content = await CurlPostAsync("https://www.chessable.com/api/v1/authenticate", json, ct);
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
            content = await CurlGetAsync(url, bearer, ct);
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
            courseContent = await CurlGetAsync(courseUrl, bearer, ct);
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
                chapterContent = await CurlGetAsync(chapterUrl, bearer, ct);
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
                        lineContent = await CurlGetAsync(lineUrl, bearer, ct);
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

    private async Task<string> CurlGetAsync(string url, string bearer, CancellationToken ct)
    {
        var args = BuildGetArgs(url, bearer);
        return await RunCurlAsync(args, null, ct);
    }

    private async Task<string> CurlPostAsync(string url, string body, CancellationToken ct)
    {
        var args = BuildPostArgs(url);
        return await RunCurlAsync(args, body, ct);
    }

    private async Task<string> RunCurlAsync(string args, string? stdinBody, CancellationToken ct)
    {
        _logger.LogDebug("curl: {Path} {Args}", _curlPath, args);

        var psi = new ProcessStartInfo
        {
            FileName = _curlPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinBody is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {_curlPath}");

        if (stdinBody is not null)
        {
            await process.StandardInput.WriteAsync(stdinBody.AsMemory(), ct);
            process.StandardInput.Close();
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("curl exited with code {Code}: {Stderr}", process.ExitCode, stderr);
        }

        return stdout;
    }

    private static string BuildGetArgs(string url, string bearer)
    {
        var sb = new StringBuilder();
        sb.Append("-s -S --compressed");
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
        sb.Append("-s -S --compressed -X POST");
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
