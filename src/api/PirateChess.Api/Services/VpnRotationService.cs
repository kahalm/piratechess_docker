using System.Text;
using System.Text.Json;

namespace PirateChess.Api.Services;

/// <inheritdoc cref="IVpnRotationService"/>
public class VpnRotationService : IVpnRotationService
{
    /// <summary>Name des un-proxied HttpClients (Control-Calls dürfen NICHT durch :8888 laufen).</summary>
    public const string ClientName = "GluetunControl";

    private const int DefaultRestartPauseMs = 3000; // wie der Crawler: kurz warten zwischen stop/running
    private const int PublicIpPollAttempts = 5; // gluetun braucht nach Reconnect kurz für die neue IP
    private const int PublicIpPollDelayMs = 1000;
    private const int ProxyReadyPollAttempts = 8;  // nach Reconnect lehnt der :8888-Tunnel kurz mit 503 ab
    private const int ProxyReadyPollDelayMs = 1000;
    private const int ProxyProbeTimeoutMs = 5000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VpnRotationService> _logger;
    private readonly string? _controlUrl;
    private readonly string? _proxyProbeUrl;
    private readonly int _rotateAfter;
    private readonly int _restartPauseMs;
    private readonly bool _enabled;

    // Serialisiert Zähler UND Rotation: keine zwei Rotationen gleichzeitig, und
    // kein Request startet, während rotiert wird (Aufrufer awaiten MaybeRotateAsync).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _requestCount;
    // Laufende (parallele) Requests. Vor einer Rotation wird auf 0 gewartet (Drain), damit kein
    // Request die IP unter sich wechseln sieht. Backstop, falls ein Request hängt.
    private int _inFlight;
    private const int DrainTimeoutMs = 60000;
    private const int DrainPollMs = 25;

    public VpnRotationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<VpnRotationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _controlUrl = configuration["Gluetun:ControlUrl"]?.TrimEnd('/');
        _rotateAfter = configuration.GetValue("Vpn:RotateAfterRequests", 20);
        if (_rotateAfter < 1) _rotateAfter = 20;

        _restartPauseMs = configuration.GetValue("Vpn:RestartPauseMs", DefaultRestartPauseMs);
        if (_restartPauseMs < 0) _restartPauseMs = DefaultRestartPauseMs;

        // Nach einer Rotation ist der gluetun-HTTP-Proxy (:8888) noch ein paar
        // Sekunden nicht bereit (CONNECT → 503). Vor dem Freigeben des Gates pollen
        // wir ihn über diesen leichten Probe-Request durch den Proxy. Leersetzen
        // (Vpn:ProxyProbeUrl="") deaktiviert das Warten.
        _proxyProbeUrl = configuration["Vpn:ProxyProbeUrl"] ?? "https://www.chessable.com/robots.txt";

        // Rotation greift nur, wenn explizit aktiviert UND ein Control-Server bekannt ist.
        _enabled = configuration.GetValue("Vpn:Enabled", true) && !string.IsNullOrEmpty(_controlUrl);

        if (_enabled)
            _logger.LogInformation(
                "VPN rotation enabled: every {N} requests via {Url}", _rotateAfter, _controlUrl);
        else
            _logger.LogInformation(
                "VPN rotation disabled (Gluetun:ControlUrl not set or Vpn:Enabled=false)");
    }

    public async Task MaybeRotateAsync(CancellationToken ct = default)
    {
        if (!_enabled) return;

        await _gate.WaitAsync(ct);
        try
        {
            _requestCount++;
            if (_requestCount >= _rotateAfter)
            {
                _requestCount = 0;
                // Erst alle bereits laufenden (parallelen) Requests abwarten, DANN rotieren —
                // sonst verlöre ein in-flight-Request mitten im Abruf die IP. Das Gate hält
                // derweil neue Requests zurück; Decrement läuft per RequestCompleted gate-frei.
                await DrainInFlightAsync(ct);
                await RotateInternalAsync(ct);
            }
            // Request als laufend anmelden (NACH einer evtl. Rotation, damit er nicht sich
            // selbst aus dem Drain blockiert). Gegenstück: RequestCompleted im finally des Aufrufers.
            Interlocked.Increment(ref _inFlight);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void RequestCompleted()
    {
        if (!_enabled) return;
        Interlocked.Decrement(ref _inFlight);
    }

    private async Task DrainInFlightAsync(CancellationToken ct)
    {
        var waited = 0;
        while (Volatile.Read(ref _inFlight) > 0)
        {
            await Task.Delay(DrainPollMs, ct);
            waited += DrainPollMs;
            if (waited >= DrainTimeoutMs)
            {
                _logger.LogWarning(
                    "VPN-Rotation: Drain-Timeout nach {Ms} ms mit {InFlight} laufenden Requests — rotiere trotzdem",
                    waited, Volatile.Read(ref _inFlight));
                break;
            }
        }
    }

    public async Task<string?> RotateNowAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_controlUrl))
        {
            _logger.LogWarning("RotateNow requested but Gluetun:ControlUrl is not configured");
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            _requestCount = 0;
            return await RotateInternalAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetPublicIpAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_controlUrl)) return null;
        var client = _httpClientFactory.CreateClient(ClientName);
        return await PollPublicIpAsync(client, ct);
    }

    /// <summary>
    /// Eigentliche Rotation: VPN stoppen → kurz warten → starten → neue IP ermitteln + loggen.
    /// Erwartet, dass der Aufrufer <see cref="_gate"/> hält. Best-effort: Fehler werden
    /// geloggt, aber nicht propagiert (eine fehlgeschlagene Rotation darf den Sync nicht killen).
    /// </summary>
    private async Task<string?> RotateInternalAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(ClientName);
        var statusUrl = $"{_controlUrl}/v1/vpn/status";

        // Sobald wir den Stop ANSTOSSEN, sind wir dafür verantwortlich, den VPN
        // wieder hochzufahren. Wird die Rotation im Fenster zwischen stop und
        // bestätigtem start abgebrochen (CancellationToken) oder scheitert die
        // start-PUT, bliebe gluetun sonst dauerhaft "stopped" liegen (→ :8888
        // liefert 503 für ALLE Requests). Das finally erzwingt dann den Neustart.
        bool needsRestart = false;

        try
        {
            _logger.LogInformation("Rotating VPN IP...");
            needsRestart = true;
            await PutVpnStatusAsync(client, statusUrl, """{"status":"stopped"}""", ct);

            await Task.Delay(_restartPauseMs, ct);

            await PutVpnStatusAsync(client, statusUrl, """{"status":"running"}""", ct);
            needsRestart = false; // Start bestätigt → kein Recovery nötig

            var newIp = await PollPublicIpAsync(client, ct);
            if (newIp is not null)
                _logger.LogInformation("VPN IP rotated → {NewIp}", newIp);
            else
                _logger.LogInformation("VPN IP rotated (neue IP nicht ermittelbar)");

            // Control-Server meldet die neue IP oft schon, während der :8888-Tunnel
            // noch 503 liefert → erst auf Proxy-Bereitschaft warten, dann Gate frei.
            await WaitForProxyReadyAsync(ct);
            return newIp;
        }
        catch (Exception ex)
        {
            // Transiente Control-/Netzwerk-Hiccups (z.B. "response ended prematurely") sind
            // erwartbar und werden von der nächsten Rotation aufgefangen → nur Message, kein Stacktrace.
            _logger.LogWarning("VPN rotation failed (non-critical): {Error}", ex.Message);
            return null;
        }
        finally
        {
            if (needsRestart)
                await EnsureVpnRunningAsync(client, statusUrl);
        }
    }

    /// <summary>
    /// Setzt den gluetun-VPN-Status (stopped/running) per Control-PUT — mit genau
    /// EINEM Retry bei Transport-Fehlern. Eine Rotation macht stop → Pause
    /// (<see cref="_restartPauseMs"/>, ~3s) → start; in dieser Pause schließt gluetun
    /// die serverseitige Keep-Alive-Verbindung. .NET greift die tote gepoolte
    /// Verbindung beim start-PUT sonst wieder auf → "Connection reset by peer"
    /// (HttpRequestException OHNE StatusCode). Der zweite Versuch öffnet eine
    /// frische Verbindung. Ein HTTP-Fehlerstatus (z.B. 503) hat einen StatusCode
    /// und wird NICHT geschluckt — der propagiert wie bisher und löst im Aufrufer
    /// das Recovery-finally aus.
    /// </summary>
    private static async Task PutVpnStatusAsync(
        HttpClient client, string statusUrl, string json, CancellationToken ct)
    {
        const int maxAttempts = 2;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var body = new StringContent(json, Encoding.UTF8, "application/json");
                (await client.PutAsync(statusUrl, body, ct)).EnsureSuccessStatusCode();
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < maxAttempts)
            {
                // Tote gepoolte Verbindung (Reset) → frischer Versuch öffnet eine neue Verbindung.
            }
        }
    }

    /// <summary>
    /// Sicherheitsnetz: fährt den VPN wieder hoch, wenn eine Rotation nach dem Stop
    /// nicht durch ein bestätigtes Start abgelöst wurde. Nutzt bewusst KEIN
    /// CancellationToken — der Restart muss auch nach einer Cancellation noch
    /// durchgehen, sonst bleibt der gluetun-Tunnel "stopped" liegen.
    /// </summary>
    private async Task EnsureVpnRunningAsync(HttpClient client, string statusUrl)
    {
        try
        {
            _logger.LogWarning(
                "VPN rotation incomplete (stopped, start not confirmed) → forcing VPN restart");
            await PutVpnStatusAsync(client, statusUrl, """{"status":"running"}""", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VPN recovery restart failed — tunnel may stay stopped");
        }
    }

    /// <summary>Pollt <c>/v1/publicip/ip</c> (gluetun braucht nach Reconnect kurz), best-effort.</summary>
    private async Task<string?> PollPublicIpAsync(HttpClient client, CancellationToken ct)
    {
        for (int attempt = 0; attempt < PublicIpPollAttempts; attempt++)
        {
            try
            {
                await Task.Delay(PublicIpPollDelayMs, ct);
                var json = await client.GetStringAsync($"{_controlUrl}/v1/publicip/ip", ct);
                var ip = ParsePublicIp(json);
                if (!string.IsNullOrWhiteSpace(ip)) return ip;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("publicip query attempt {Attempt} failed: {Error}", attempt + 1, ex.Message);
            }
        }
        return null;
    }

    /// <summary>
    /// Pollt nach einer Rotation den gluetun-HTTP-Proxy (:8888) über einen leichten
    /// Probe-Request DURCH den Proxy, bis der CONNECT-Tunnel wieder steht
    /// (Antwort ≠ 503). gluetun lehnt während des Reconnects mit 503 ab — der
    /// unmittelbar folgende Chessable-Request käme sonst mit „CONNECT tunnel failed,
    /// response 503" leer zurück. Best-effort: gibt nach
    /// <see cref="ProxyReadyPollAttempts"/> Versuchen auf und fährt fort.
    /// </summary>
    private async Task WaitForProxyReadyAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_proxyProbeUrl))
            return;

        // Der "Chessable"-Client ist auf den gluetun-Proxy (:8888) verdrahtet.
        var client = _httpClientFactory.CreateClient(ChessableHttpClientFactory.ClientName);

        for (int attempt = 0; attempt < ProxyReadyPollAttempts; attempt++)
        {
            int status = 0;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ProxyProbeTimeoutMs);
                using var req = new HttpRequestMessage(HttpMethod.Head, _proxyProbeUrl);
                using var resp = await client.SendAsync(req, timeoutCts.Token);
                status = (int)resp.StatusCode;
            }
            catch (Exception ex)
            {
                // Tunnel noch nicht bereit (503 beim CONNECT → Exception) oder Timeout — erwartbar.
                _logger.LogDebug("proxy readiness probe attempt {Attempt} failed: {Error}", attempt + 1, ex.Message);
            }

            if (IsProxyReady(status))
            {
                _logger.LogInformation(
                    "Proxy tunnel ready after rotation (probe status {Status}, attempt {Attempt})",
                    status, attempt + 1);
                return;
            }

            await Task.Delay(ProxyReadyPollDelayMs, ct);
        }

        _logger.LogWarning(
            "Proxy tunnel not confirmed ready after {Attempts} attempts post-rotation — proceeding anyway",
            ProxyReadyPollAttempts);
    }

    /// <summary>
    /// Entscheidet anhand des HTTP-Statuscodes eines Probe-Requests, ob der gluetun-
    /// Proxy-Tunnel bereit ist. 0 = Request warf (Tunnel down / Timeout) → nicht bereit;
    /// 503 = gluetun lehnt CONNECT während des Reconnects ab → nicht bereit; alles
    /// andere (200/403/404/405 …) = Origin durch den Tunnel erreicht → bereit.
    /// </summary>
    public static bool IsProxyReady(int httpStatusCode)
        => httpStatusCode > 0 && httpStatusCode != 503;

    /// <summary>Extrahiert <c>public_ip</c> aus der gluetun-Antwort von <c>/v1/publicip/ip</c>.</summary>
    public static string? ParsePublicIp(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("public_ip", out var ipEl)
                && ipEl.ValueKind == JsonValueKind.String)
            {
                var ip = ipEl.GetString();
                return string.IsNullOrWhiteSpace(ip) ? null : ip;
            }
        }
        catch (JsonException) { /* keine gültige JSON → null */ }
        return null;
    }
}
