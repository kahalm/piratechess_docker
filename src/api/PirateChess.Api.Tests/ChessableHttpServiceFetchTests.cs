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

    private ChessableHttpService Build(int parallel = 1)
    {
        var script = Path.Combine(_dir, "curl");
        File.WriteAllText(script, $$"""
            #!/bin/bash
            dir='{{_dir}}'
            url="${@: -1}"
            printf '%s\n' "$url" >> "$dir/calls.log"
            case "$url" in
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
        return new ChessableHttpService(NullLogger<ChessableHttpService>.Instance, _scopeFactory, new NoVpn(), lineCache, config)
        {
            CurlPath = script,
            ProxyRetryDelayMs = 0,
        };
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

    private sealed class NoVpn : IVpnRotationService
    {
        private static VpnLease Lease() => new(null, _ => { });
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
