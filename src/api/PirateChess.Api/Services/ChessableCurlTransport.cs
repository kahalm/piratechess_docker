using System.Diagnostics;
using Serilog.Context;
using static PirateChess.Api.Services.ChessableCurlArgs;
using static PirateChess.Api.Services.ChessableResponseClassifier;

namespace PirateChess.Api.Services;

/// <summary>
/// Ein Chessable-Request über curl: VPN-Tunnel leihen, Argumente bauen, <see cref="ICurlRunner"/> ausführen, eine
/// Info-Zeile je Request loggen, die Rohantwort auditieren (<see cref="RawResponseAudit"/>), Soft-Block melden und
/// einen transienten Proxy-Ausfall als <see cref="ProxyTunnelException"/> werfen. Aus <see cref="ChessableHttpService"/>
/// herausgelöst (Zerlegung Z46); loggt unter dessen Kategorie, damit Log-Filter gleich bleiben.
/// </summary>
internal sealed class ChessableCurlTransport
{
    private readonly ILogger<ChessableHttpService> _logger;
    private readonly RawResponseAudit _audit;
    private readonly IVpnRotationService _vpn;
    private readonly ICurlRunner _curl;
    private readonly int _requestMaxTimeSec;
    private readonly bool _rotateOnBlock;

    public ChessableCurlTransport(ILogger<ChessableHttpService> logger, RawResponseAudit audit, IVpnRotationService vpn,
        ICurlRunner curl, int requestMaxTimeSec, bool rotateOnBlock)
    {
        _logger = logger;
        _audit = audit;
        _vpn = vpn;
        _curl = curl;
        _requestMaxTimeSec = requestMaxTimeSec;
        _rotateOnBlock = rotateOnBlock;
    }

    /// <param name="coupleToken">Ob dieser Request die token-gekoppelte IP-Rotation auslösen darf.
    /// Nur der tiefe Kurs-Abruf (Kurs/Kapitel/Linien) koppelt; leichte Einzel-Requests (Kursliste,
    /// Bearer-Test, Zeilen-Schätzung, Debug) laufen entkoppelt mit: sonst erzwingt ein einziger
    /// Fremd-Request mitten in einem Import zwei volle Rotationen (hin und zurück) und bremst ihn aus.</param>
    public async Task<string> GetAsync(string url, string bearer, string endpoint, string? chessableUid, CancellationToken ct, int? pinnedTunnel = null, bool coupleToken = true)
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

    public async Task<string> PostAsync(string url, string body, string endpoint, string? chessableUid, CancellationToken ct)
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
}
