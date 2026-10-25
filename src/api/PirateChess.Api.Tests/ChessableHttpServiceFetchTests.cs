using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PirateChess.Api.Data;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>
/// Der tiefe Kursabruf (FetchCourseDataAsync) gegen ein Fake-curl, das Chessable-Antworten aus Dateien ausspielt.
/// Anlass (S2-001): Chessable antwortet bei abgelaufenem/gesperrtem Token mit HTTP 200 und
/// <c>{"error":{...}}</c>. Auf Kapitel- und Linienebene ging das als leeres Kapitel bzw. gültige Linie durch, der
/// Abruf meldete Erfolg, und der Rumpf-Kurs landete für alle Nutzer im geteilten Cache.
/// </summary>
public sealed class ChessableHttpServiceFetchTests : IDisposable
{
    private const string ValidLine = "{\"game\":{\"color\":\"white\"}}";
    private const string ExpiredToken = "{\"error\":{\"message\":\"Expired token\"}}";
    private const string Banned = "{\"error\":{\"message\":\"User is banned or deleted\"}}";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pc-fakecurl-" + Guid.NewGuid().ToString("N"));
    private readonly IServiceScopeFactory _scopeFactory;

    public ChessableHttpServiceFetchTests()
    {
        Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        // Kurs: Kapitel 1 (Linien 11, 12), Kapitel 2 (Linie 21) — alles gültig, einzelne Tests überschreiben.
        Respond("course", "{\"course\":{\"data\":[{\"id\":1,\"total\":2},{\"id\":2,\"total\":1}]}}");
        Respond("chapter-1", "{\"list\":{\"name\":\"K1\",\"data\":[{\"id\":11},{\"id\":12}]}}");
        Respond("chapter-2", "{\"list\":{\"name\":\"K2\",\"data\":[{\"id\":21}]}}");
        Respond("line-11", ValidLine);
        Respond("line-12", ValidLine);
        Respond("line-21", ValidLine);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* Aufräumen ist best effort */ }
    }

    private void Respond(string key, string body) => File.WriteAllText(Path.Combine(_dir, key + ".json"), body);

    private int Calls(string urlPart)
    {
        var log = Path.Combine(_dir, "calls.log");
        return File.Exists(log) ? File.ReadAllLines(log).Count(l => l.EndsWith(urlPart, StringComparison.Ordinal)) : 0;
    }

    /// <param name="proxy">Proxy des VPN-Leases (landet als „--proxy" vorn in der Argumentliste).</param>
    /// <param name="record">Fake-curl schreibt je Lauf argv (NUL-getrennt wie /proc/&lt;pid&gt;/cmdline) und stdin mit,
    /// siehe <see cref="Recorded"/>.</param>
    /// <param name="runner">Statt des Prozess-Runners mit Fake-curl ein In-Memory-<see cref="ICurlRunner"/>.</param>
    private ChessableHttpService Build(int parallel = 1, string? proxy = null, bool record = false, ICurlRunner? runner = null)
    {
        var script = Path.Combine(_dir, "curl");
        var recordLines = record ? """
            n=$(cat "$dir/n" 2>/dev/null || echo 0); echo $((n+1)) > "$dir/n"
            printf '%s\0' "$@" > "$dir/call-$n.argv"
            timeout 5 cat > "$dir/call-$n.stdin"
            """ : "";
        File.WriteAllText(script, $$"""
            #!/bin/bash
            dir='{{_dir}}'
            url="${@: -1}"
            printf '%s\n' "$url" >> "$dir/calls.log"
            {{recordLines}}
            case "$url" in
              *authenticate*) f=login ;;
              *getCourse*) f=course ;;
              *getList*) f="chapter-${url##*lid=}" ;;
              *getGame*) f="line-${url##*oid=}" ;;
              *) f=none ;;
            esac
            cat "$dir/$f.json" 2>/dev/null
            exit 0
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chessable:InterRequestDelayMaxMs"] = "1",
            ["Chessable:BlockRetryDelayMs"] = "0",
            ["Chessable:ParallelLineFetches"] = parallel.ToString(),
        }).Build();
        var lineCache = new RawLineCache(_scopeFactory, NullLogger<RawLineCache>.Instance);
        runner ??= new CurlRunner(NullLogger<CurlRunner>.Instance) { CurlPath = script };
        var audit = new RawResponseAudit(_scopeFactory, NullLogger<RawResponseAudit>.Instance);
        return new ChessableHttpService(NullLogger<ChessableHttpService>.Instance, audit, new NoVpn(proxy), lineCache, runner, config)
        {
            ProxyRetryDelayMs = 0,
        };
    }

    /// <summary>argv und stdin jedes aufgezeichneten curl-Laufs, in Aufruf-Reihenfolge (<c>Build(record: true)</c>).</summary>
    private List<(string[] Argv, string Stdin)> Recorded()
    {
        var n = int.Parse(File.ReadAllText(Path.Combine(_dir, "n")).Trim());
        return Enumerable.Range(0, n).Select(i => (
            File.ReadAllText(Path.Combine(_dir, $"call-{i}.argv")).TrimEnd('\0').Split('\0'),
            File.ReadAllText(Path.Combine(_dir, $"call-{i}.stdin")))).ToList();
    }

    private async Task<List<string>> AuditedEndpointsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChessableRawResponses
            .OrderBy(r => r.Id).Select(r => r.Endpoint).ToListAsync();
    }

    private async Task<bool> LineCachedAsync(int oid)
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().CachedRawLines.AnyAsync(l => l.Oid == oid);
    }

    [Fact]
    public async Task Fetch_AllValid_ReturnsTheWholeCourse()
    {
        if (!OperatingSystem.IsLinux()) return;   // Fake-curl ist ein Bash-Skript

        var (data, error) = await Build().FetchCourseDataAsync("bearer", "1", "777");

        Assert.Null(error);
        Assert.Equal(2, data!.ChapterList.Count);
        Assert.Equal(new[] { 11, 12 }, data.ChapterList[0].ResponseLineList.Select(l => l.Oid));
        Assert.Equal(ValidLine, data.ChapterList[1].ResponseLineList[0].LineJsonContent);
        Assert.True(await LineCachedAsync(21));
        using var scope = _scopeFactory.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().CachedRawLines.SingleAsync(l => l.Oid == 21);
        Assert.Equal("777", row.Bid);            // Linie gehört zu dem Kurs, für den der Server sie geholt hat
        Assert.False(row.FromBrowser);
    }

    // Token läuft mitten im Import ab: getList liefert den Fehlerkörper. Vorher: leeres Kapitel, Erfolg.
    [Fact]
    public async Task Fetch_ChapterWithExpiredToken_FailsAtOnce()
    {
        if (!OperatingSystem.IsLinux()) return;
        Respond("chapter-2", ExpiredToken);

        var (data, error) = await Build().FetchCourseDataAsync("bearer", "1", "777");

        Assert.Null(data);
        Assert.Contains("Expired token", error);
        Assert.Contains("neu hinterlegen", error);
        Assert.Equal(1, Calls("lid=2"));             // Token tot → kein Retry
    }

    // Kein Token-Fehler: Fehlversuch mit Retry, nach dem letzten Versuch Abbruch statt Kapitel ohne Linien.
    [Fact]
    public async Task Fetch_ChapterWithOtherChessableError_RetriesThenFails()
    {
        if (!OperatingSystem.IsLinux()) return;
        Respond("chapter-2", Banned);

        var (data, error) = await Build().FetchCourseDataAsync("bearer", "1", "777");

        Assert.Null(data);
        Assert.Contains("User is banned or deleted", error);
        Assert.Equal(4, Calls("lid=2"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Fetch_LineWithExpiredToken_FailsAtOnce_AndStopsTheImport(int parallel)
    {
        if (!OperatingSystem.IsLinux()) return;
        Respond("line-12", ExpiredToken);

        var (data, error) = await Build(parallel).FetchCourseDataAsync("bearer", "1", "777");

        Assert.Null(data);
        Assert.Contains("Expired token", error);
        Assert.Equal(1, Calls("oid=12"));
        Assert.Equal(0, Calls("lid=2"));             // der Rest des Kurses wird nicht mehr geholt
        Assert.False(await LineCachedAsync(12));
    }

    // Prod 30.06.: 31 Linien „User is banned or deleted" gingen als gültige Linien durch.
    [Fact]
    public async Task Fetch_LineWithOtherChessableError_RetriesThenFails_NothingCached()
    {
        if (!OperatingSystem.IsLinux()) return;
        Respond("line-12", Banned);

        var (data, error) = await Build().FetchCourseDataAsync("bearer", "1", "777");

        Assert.Null(data);
        Assert.Contains("User is banned or deleted", error);
        Assert.Contains("12", error);
        Assert.Equal(10, Calls("oid=12"));
        Assert.False(await LineCachedAsync(12));
    }

    // Eine Antwort ohne game-Objekt ist kein Erfolg mehr: Retry, und die Linie landet nicht im Linien-Cache.
    [Fact]
    public async Task Fetch_LineWithoutGameObject_IsRetried_AndNotCached()
    {
        if (!OperatingSystem.IsLinux()) return;
        Respond("line-12", "{\"x\":1}");

        var (data, error) = await Build().FetchCourseDataAsync("bearer", "1", "777");

        Assert.Null(error);                           // wie eine abgeschnittene Linie: übersprungen, Kurs unvollständig
        Assert.Equal(10, Calls("oid=12"));
        Assert.False(await LineCachedAsync(12));
        Assert.False(RawCourseCache.IsComplete(data));
    }

    // S2-006: Der Bearer stand als „-H authorization: Bearer …" in der Argumentliste des curl-Prozesses und damit
    // in /proc/<pid>/cmdline (für jedes lokale Konto des Docker-Hosts lesbar). Jetzt geht er über stdin („-H @-").
    [Fact]
    public async Task CurlGet_BearerNotInProcessArguments_ButOnStdin()
    {
        if (!OperatingSystem.IsLinux()) return;
        var service = Build();
        // Fake-curl durch eines ersetzen, das argv (NUL-getrennt wie /proc/<pid>/cmdline) und stdin mitschreibt.
        File.WriteAllText(Path.Combine(_dir, "curl"), $$"""
            #!/bin/bash
            dir='{{_dir}}'
            printf '%s\0' "$@" > "$dir/argv.bin"
            timeout 5 cat > "$dir/stdin.txt"
            printf '%s' '{{ValidLine}}'
            exit 0
            """.Replace("\r\n", "\n"));
        const string bearer = "eyJhbGciOiJIUzI1NiJ9.eyJ1aWQiOjF9.S2006-SECRET-SIGNATURE";

        var (ok, _, _, error, _) = await service.DebugFetchLineAsync(bearer, "1", 42);

        Assert.Null(error);
        Assert.True(ok);
        var argv = File.ReadAllText(Path.Combine(_dir, "argv.bin")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(argv, a => a.Contains("S2006-SECRET-SIGNATURE", StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a.Contains("authorization", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("https://www.chessable.com/api/v1/getGame?lng=en&uid=1&oid=42", argv[^1]);
        Assert.Equal($"authorization: Bearer {bearer}\n", File.ReadAllText(Path.Combine(_dir, "stdin.txt")));
    }

    // Seit S2-006 schreibt jeder GET auf stdin. Beendet sich curl, ohne stdin zu lesen (hier: Proxy-503), darf der
    // Broken pipe beim Schreiben den Ausgang nicht verdecken: der Proxy-Fehler muss weiter den Retry auslösen.
    [Fact]
    public async Task CurlGet_CurlExitsWithoutReadingStdin_ProxyFailureStillRetried()
    {
        if (!OperatingSystem.IsLinux()) return;
        var service = Build();
        File.WriteAllText(Path.Combine(_dir, "curl"), $$"""
            #!/bin/bash
            printf '%s\n' "${@: -1}" >> '{{_dir}}/calls.log'
            echo 'curl: (56) CONNECT tunnel failed, response 503' >&2
            exit 56
            """.Replace("\r\n", "\n"));
        var hugeBearer = new string('x', 256 * 1024);   // größer als der Pipe-Puffer → Schreiben scheitert sicher

        var (data, error) = await service.FetchCourseDataAsync(hugeBearer, "1", "777");

        Assert.Null(data);
        Assert.Contains("proxy tunnel unavailable", error);
        Assert.Equal(4, Calls("bid=777&includeVariations=true"));
    }

    // Golden-Test (S2-014, vor dem Schnitt aufgezeichnet): argv und stdin JEDES curl-Laufs eines kompletten Kursabrufs,
    // in Reihenfolge, samt Audit-Zeile je Lauf. Der Umbau (Prozessstart hinter ICurlRunner) darf daran kein Byte ändern:
    // die Argument-Reihenfolge ist der TLS-/Header-Fingerprint, der Proxy steht vorn, der Bearer nur auf stdin.
    [Fact]
    public async Task Fetch_CompleteCourse_CurlArgvAndStdinSequence_IsGolden()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string proxy = "http://gluetun:8888";
        const string bearer = "golden.bearer.token";

        var (data, error) = await Build(proxy: proxy, record: true).FetchCourseDataAsync(bearer, "1", "777");

        Assert.Null(error);
        Assert.Equal(2, data!.ChapterList.Count);
        const string api = "https://www.chessable.com/api/v1/";
        var urls = new[]
        {
            api + "getCourse?uid=1&bid=777&includeVariations=true",
            api + "getList?uid=1&bid=777&lid=1",
            api + "getGame?lng=en&uid=1&oid=11",
            api + "getGame?lng=en&uid=1&oid=12",
            api + "getList?uid=1&bid=777&lid=2",
            api + "getGame?lng=en&uid=1&oid=21",
        };
        var recorded = Recorded();
        Assert.Equal(urls.Length, recorded.Count);
        for (int i = 0; i < urls.Length; i++)
        {
            Assert.Equal(["--proxy", proxy, .. ChessableHttpServiceTests.GoldenGetArgv(urls[i], "20")], recorded[i].Argv);
            Assert.Equal($"authorization: Bearer {bearer}\n", recorded[i].Stdin);
        }
        Assert.Equal(["course", "chapter", "line", "line", "chapter", "line"], await AuditedEndpointsAsync());
    }

    // Golden-Test des Login-POST: Body geht über stdin („-d @-"), der Proxy steht vorn, das frische JWT der Antwort
    // wird im Audit redigiert.
    [Fact]
    public async Task Login_CurlArgvAndStdin_IsGolden_JwtRedactedInAudit()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string proxy = "http://gluetun:8888";
        Respond("login", "{\"jwt\":\"fresh.login.jwt\"}");

        var (jwt, error) = await Build(proxy: proxy, record: true).LoginAsync("a@b.c", "pw");

        Assert.Null(error);
        Assert.Equal("fresh.login.jwt", jwt);
        const string url = "https://www.chessable.com/api/v1/authenticate";
        var recorded = Assert.Single(Recorded());
        Assert.Equal(["--proxy", proxy, .. ChessableCurlArgs.BuildPostArgs(url)], recorded.Argv);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA512.HashData("pw"u8));
        Assert.Equal("{\"method\":\"email\",\"credentials\":{\"email\":\"a@b.c\",\"password\":\"" + hash
            + "\"},\"providerData\":null,\"mode\":\"login\",\"checkoutData\":null,\"preferredLanguage\":\"en\","
            + "\"newsletterChecked\":false}", recorded.Stdin);
        using var scope = _scopeFactory.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChessableRawResponses.SingleAsync();
        Assert.Equal("login", row.Endpoint);
        Assert.Equal("{\"jwt\":\"[redacted]\"}", GzipText.Decompress(row.RawJson));
    }

    // S2-014: der Abruf ist ohne echtes curl testbar — ICurlRunner ist die Naht zwischen Orchestrierung und Prozess.
    // Hier (plattformunabhängig, kein Prozess): der Proxy meldet beim ersten Kurs-Abruf 503 (curl 56), der Retry läuft,
    // danach kommt der ganze Kurs; der Runner sieht je Lauf dieselben Argumente, den Proxy und den Bearer auf stdin.
    [Fact]
    public async Task Fetch_WithFakeCurlRunner_NoProcess_Proxy503OnCourseIsRetried()
    {
        const string proxy = "http://gluetun:8888";
        var courseCalls = 0;
        var runner = new FakeCurlRunner(url =>
        {
            if (url.Contains("getCourse") && ++courseCalls == 1)
                return new CurlResult(56, "", "curl: (56) CONNECT tunnel failed, response 503");
            if (url.Contains("getCourse")) return new CurlResult(0, "{\"course\":{\"data\":[{\"id\":1,\"total\":1}]}}", "");
            if (url.Contains("getList")) return new CurlResult(0, "{\"list\":{\"name\":\"K1\",\"data\":[{\"id\":11}]}}", "");
            return new CurlResult(0, ValidLine, "");
        });

        var (data, error) = await Build(proxy: proxy, runner: runner).FetchCourseDataAsync("fake.bearer", "1", "777");

        Assert.Null(error);
        Assert.Equal(ValidLine, Assert.Single(Assert.Single(data!.ChapterList).ResponseLineList).LineJsonContent);
        Assert.Equal(4, runner.Calls.Count);                  // Kurs (503), Kurs, Kapitel, Linie
        Assert.All(runner.Calls, c =>
        {
            Assert.Equal(ChessableHttpServiceTests.GoldenGetArgv(c.Args[^1], "20"), c.Args);
            Assert.Equal(proxy, c.ProxyUrl);
            Assert.Equal("authorization: Bearer fake.bearer\n", c.Stdin);
        });
        Assert.Equal(["course", "course", "chapter", "line"], await AuditedEndpointsAsync());
    }

    // --- Charakterisierung vor der Zerlegung Z46 (Kursabruf in ChessableCourseFetcher): Fortschritt, Retry-Texte,
    // Strukturfehler, Linien-Cache. Alles über den In-Memory-Runner, ohne Prozess und ohne Chessable.

    private static FakeCurlRunner ValidCourseRunner(Func<string, CurlResult?>? overrideFor = null) => new(url =>
    {
        if (overrideFor?.Invoke(url) is { } r) return r;
        if (url.Contains("getCourse")) return new CurlResult(0, "{\"course\":{\"data\":[{\"id\":1,\"total\":2},{\"id\":2,\"total\":1}]}}", "");
        if (url.EndsWith("lid=1")) return new CurlResult(0, "{\"list\":{\"name\":\"K1\",\"data\":[{\"id\":11},{\"id\":12}]}}", "");
        if (url.EndsWith("lid=2")) return new CurlResult(0, "{\"list\":{\"name\":\"K2\",\"data\":[{\"id\":21}]}}", "");
        return new CurlResult(0, ValidLine, "");
    });

    [Fact]
    public async Task Fetch_ProgressCallbacks_FireInTheSameOrderAndFormat()
    {
        var events = new List<string>();
        var (data, error) = await Build(runner: ValidCourseRunner()).FetchCourseDataAsync("b", "1", "777", new CourseFetchOptions
        {
            OnChapterProgress = s => events.Add("chapter " + s),
            OnLineProgress = s => events.Add("line " + s),
            OnCumulativeLines = s => events.Add("cum " + s),
            OnRetry = s => events.Add("retry " + s),
            OnTotalLines = n => events.Add("total " + n),
        });

        Assert.Null(error);
        Assert.Equal(2, data!.ChapterList.Count);
        Assert.Equal(new[]
        {
            "total 3",
            "chapter 1 / 2", "cum 1", "line 1 / 2", "cum 2", "line 2 / 2",
            "chapter 2 / 2", "cum 3", "line 1 / 1",
        }, events);
    }

    [Fact]
    public async Task Fetch_LineSoftBlockedTenTimes_IsSkippedWithRetryTexts_NotCached()
    {
        var retries = new List<string>();
        var runner = ValidCourseRunner(url => url.EndsWith("oid=12") ? new CurlResult(0, "{}", "") : null);

        var (data, error) = await Build(runner: runner).FetchCourseDataAsync("b", "1", "777", new CourseFetchOptions { OnRetry = retries.Add });

        Assert.Null(error);
        var line = data!.ChapterList[0].ResponseLineList[1];
        Assert.Equal(12, line.Oid);
        Assert.Equal("{}", line.LineJsonContent);
        Assert.Equal(10, runner.Calls.Count(c => c.Args[^1].EndsWith("oid=12")));
        Assert.Equal(Enumerable.Range(1, 9).Select(i => $"[002.003] Retry {i}/10 ...")
            .Append("[002.003] FAILED after 10 attempts, skipping."), retries);
        Assert.False(await LineCachedAsync(12));
        Assert.True(await LineCachedAsync(11));
    }

    [Fact]
    public async Task Fetch_ChapterTruncatedFourTimes_BecomesEmptyChapter_WithRetryTexts()
    {
        var retries = new List<string>();
        const string truncated = "{\"list\":{\"name\":\"K2\",\"data\":[{\"id\":2";
        var runner = ValidCourseRunner(url => url.EndsWith("lid=2") ? new CurlResult(0, truncated, "") : null);

        var (data, error) = await Build(runner: runner).FetchCourseDataAsync("b", "1", "777", new CourseFetchOptions { OnRetry = retries.Add });

        Assert.Null(error);
        Assert.Equal(2, data!.ChapterList.Count);
        Assert.Empty(data.ChapterList[1].ResponseLineList);
        Assert.Equal(truncated, data.ChapterList[1].ChapterJsonContent);
        Assert.Equal(4, runner.Calls.Count(c => c.Args[^1].EndsWith("lid=2")));
        Assert.Equal(Enumerable.Range(1, 3).Select(i => $"Kapitel 2: unvollständig, Retry {i}/4 ..."), retries);
    }

    public static TheoryData<string, int, string, int> StructureFailures => new()
    {
        // Antwort von getCourse (immer gleich), curl-Exit, erwarteter Fehler, erwartete getCourse-Aufrufe
        { "", 0, "Empty course response", 4 },
        { "{}", 0, "Empty course response", 4 },
        { "{\"error\":{\"message\":\"Expired token\"}}", 0,
            "Chessable-Token abgelaufen/ungültig (Expired token) — bitte den Bearer neu hinterlegen.", 1 },
        { "<html><body>you have been blocked</body></html>", 0,
            "Zugriff von Chessable/Cloudflare blockiert (HTTP 403). Der Token ist nicht abgelaufen → " +
            "sehr wahrscheinlich ist die VPN-Ausgangs-IP gesperrt: IP rotieren bzw. VPN-Server wechseln. " +
            "Falls das nicht hilft, den Bearer prüfen.", 1 },
        { "{\"course\":{\"data\":[{\"id\":", 0, "Failed to parse course JSON", 1 },
        { "{\"course\":{\"data\":[]}}", 0, "Course has no chapters", 1 },
        { "", 56, "Failed to fetch course (proxy tunnel unavailable): curl: (56) CONNECT tunnel failed, response 503", 4 },
    };

    [Theory]
    [MemberData(nameof(StructureFailures))]
    public async Task Fetch_StructureFailure_SameErrorAndAttempts(string body, int exitCode, string expected, int courseCalls)
    {
        var stderr = exitCode == 0 ? "" : "curl: (56) CONNECT tunnel failed, response 503";
        var runner = new FakeCurlRunner(_ => new CurlResult(exitCode, body, stderr));

        var (data, error) = await Build(runner: runner).FetchCourseDataAsync("b", "1", "777");

        Assert.Null(data);
        Assert.Equal(expected, error);
        Assert.Equal(courseCalls, runner.Calls.Count);
    }

    [Fact]
    public async Task Fetch_RunnerThrows_StructureFailsWithMessage()
    {
        var runner = new FakeCurlRunner(_ => throw new InvalidOperationException("kein curl"));
        var (data, error) = await Build(runner: runner).FetchCourseDataAsync("b", "1", "777");
        Assert.Null(data);
        Assert.Equal("Failed to fetch course: kein curl", error);
        Assert.Equal(["course"], await AuditedEndpointsAsync());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Fetch_CachedLine_IsReusedUnlessBypassed(bool bypass, int expectedLineCalls)
    {
        const string cachedLine = "{\"game\":{\"color\":\"black\"}}";
        await new RawLineCache(_scopeFactory, NullLogger<RawLineCache>.Instance).SetAsync(11, cachedLine, "777");
        var runner = ValidCourseRunner();

        var (data, error) = await Build(runner: runner).FetchCourseDataAsync("b", "1", "777", new CourseFetchOptions { BypassLineCache = bypass });

        Assert.Null(error);
        Assert.Equal(expectedLineCalls, runner.Calls.Count(c => c.Args[^1].EndsWith("oid=11")));
        Assert.Equal(bypass ? ValidLine : cachedLine, data!.ChapterList[0].ResponseLineList[0].LineJsonContent);
    }

    [Fact]
    public async Task CourseLineCount_SumsVariations_FallsBackToTotal()
    {
        var runner = new FakeCurlRunner(_ => new CurlResult(0,
            "{\"course\":{\"data\":[{\"id\":1,\"total\":5,\"variations\":[{},{}]},{\"id\":2,\"total\":3}]}}", ""));
        var (total, error) = await Build(runner: runner).GetCourseLineCountAsync("b", "1", "777");
        Assert.Null(error);
        Assert.Equal(5, total);
        Assert.Single(runner.Calls);
    }

    /// <summary>In-Memory-<see cref="ICurlRunner"/>: antwortet je URL (letztes Argument) und zeichnet jeden Lauf auf.</summary>
    private sealed class FakeCurlRunner(Func<string, CurlResult> respond) : ICurlRunner
    {
        public List<(List<string> Args, string? Stdin, string? ProxyUrl)> Calls { get; } = [];

        public Task<CurlResult> RunAsync(IReadOnlyList<string> args, string? stdin, string? proxyUrl, CancellationToken ct)
        {
            lock (Calls) Calls.Add((args.ToList(), stdin, proxyUrl));
            return Task.FromResult(respond(args[^1]));
        }
    }

    private sealed class NoVpn(string? proxy = null) : IVpnRotationService
    {
        private VpnLease Lease() => new(proxy, _ => { });
        public Task<VpnLease> AcquireAsync(CancellationToken ct = default) => Task.FromResult(Lease());
        public Task<VpnLease> AcquireAsync(string? chessableUid, CancellationToken ct = default) => Task.FromResult(Lease());
        public Task<VpnLease> AcquireSpecificAsync(int index, CancellationToken ct = default) => Task.FromResult(Lease());
        public int TunnelCount => 1;
        public IReadOnlyList<VpnTunnelStatus> DescribeTunnels() => [];
        public Task<string?> GetTunnelPublicIpAsync(int index, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> RotateNowAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetPublicIpAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
