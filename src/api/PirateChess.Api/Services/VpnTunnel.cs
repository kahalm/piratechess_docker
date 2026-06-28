using System.Net;
using System.Text;
using System.Text.Json;

namespace PirateChess.Api.Services;

/// <summary>
/// EIN VPN-Tunnel (= ein gluetun-Container): eigener HTTP-Proxy (<see cref="ProxyUrl"/>) +
/// eigener Control-Server (<paramref name="controlUrl"/>) + eigener Request-Zähler/Rotations-Gate.
/// Mehrere Tunnel werden vom <see cref="VpnRotationService"/>-Pool round-robin verteilt, sodass
/// während ein Tunnel rotiert ein anderer weiterliefern kann. Die Rotation ist drain-aware:
/// vor dem IP-Wechsel wird auf 0 laufende Requests gewartet (kein Wechsel mitten im Request).
/// </summary>
internal sealed class VpnTunnel
{
    private const int DefaultRestartPauseMs = 3000;
    private const int PublicIpPollAttempts = 5;
    private const int PublicIpPollDelayMs = 1000;
    private const int ProxyReadyPollAttempts = 8;
    private const int ProxyReadyPollDelayMs = 1000;
    private const int ProxyProbeTimeoutMs = 5000;
    private const int DrainTimeoutMs = 60000;
    private const int DrainPollMs = 25;

    public string? ProxyUrl { get; }
    private readonly string? _controlUrl;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly int _rotateAfter;
    private readonly int _restartPauseMs;
    private readonly string? _proxyProbeUrl;
    private readonly bool _enabled;
    private readonly string _label;
    private readonly HttpClient? _probeClient;   // durch DIESEN Tunnel-Proxy (für Readiness-Probe)

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _requestCount;
    private int _inFlight;

    public VpnTunnel(string? proxyUrl, string? controlUrl, IHttpClientFactory httpClientFactory,
        IConfiguration cfg, ILogger logger, int index, int tunnelCount)
    {
        ProxyUrl = string.IsNullOrWhiteSpace(proxyUrl) ? null : proxyUrl;
        _controlUrl = string.IsNullOrWhiteSpace(controlUrl) ? null : controlUrl.TrimEnd('/');
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _label = $"Tunnel#{index}";

        _rotateAfter = cfg.GetValue("Vpn:RotateAfterRequests", 20);
        if (_rotateAfter < 1) _rotateAfter = 20;

        // Stagger: Start-Zähler pro Tunnel versetzen, damit die Tunnel NICHT gleichzeitig rotieren
        // (sonst stehen bei round-robin alle zugleich → Stall). Tunnel i (0-basiert) startet bei
        // i*rotateAfter/count → die Rotationen verteilen sich gleichmäßig, immer ist einer oben.
        if (tunnelCount > 1)
            _requestCount = (index - 1) * _rotateAfter / tunnelCount;

        _restartPauseMs = cfg.GetValue("Vpn:RestartPauseMs", DefaultRestartPauseMs);
        if (_restartPauseMs < 0) _restartPauseMs = DefaultRestartPauseMs;
        _proxyProbeUrl = cfg["Vpn:ProxyProbeUrl"] ?? "https://www.chessable.com/robots.txt";

        _enabled = cfg.GetValue("Vpn:Enabled", true) && _controlUrl is not null;

        // Readiness-Probe muss durch DIESEN Tunnel-Proxy laufen (nicht den ersten).
        if (ProxyUrl is not null)
        {
            var handler = new HttpClientHandler { Proxy = new WebProxy(ProxyUrl), UseProxy = true };
            _probeClient = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(ProxyProbeTimeoutMs + 2000) };
        }

        _logger.LogInformation("{Label}: proxy={Proxy} control={Control} rotation={Enabled} (every {N}, start@{Offset})",
            _label, ProxyUrl ?? "none", _controlUrl ?? "none", _enabled, _rotateAfter, _requestCount);
    }

    /// <summary>Zählt den Request mit, rotiert ggf. (drain-aware) und meldet ihn als laufend an.</summary>
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
                await DrainInFlightAsync(ct);
                await RotateInternalAsync(ct);
            }
            Interlocked.Increment(ref _inFlight);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Gegenstück zu <see cref="MaybeRotateAsync"/> (im finally des Aufrufers).</summary>
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
                _logger.LogWarning("{Label}: Drain-Timeout nach {Ms} ms mit {InFlight} Requests — rotiere trotzdem",
                    _label, waited, Volatile.Read(ref _inFlight));
                break;
            }
        }
    }

    public async Task<string?> RotateNowAsync(CancellationToken ct = default)
    {
        if (_controlUrl is null) return null;
        await _gate.WaitAsync(ct);
        try { _requestCount = 0; return await RotateInternalAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetPublicIpAsync(CancellationToken ct = default)
    {
        if (_controlUrl is null) return null;
        var client = _httpClientFactory.CreateClient(VpnRotationService.ClientName);
        return await PollPublicIpAsync(client, ct);
    }

    private async Task<string?> RotateInternalAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(VpnRotationService.ClientName);
        var statusUrl = $"{_controlUrl}/v1/vpn/status";
        bool needsRestart = false;
        try
        {
            _logger.LogInformation("{Label}: Rotating VPN IP...", _label);
            needsRestart = true;
            await PutVpnStatusAsync(client, statusUrl, """{"status":"stopped"}""", ct);
            await Task.Delay(_restartPauseMs, ct);
            await PutVpnStatusAsync(client, statusUrl, """{"status":"running"}""", ct);
            needsRestart = false;

            var newIp = await PollPublicIpAsync(client, ct);
            _logger.LogInformation("{Label}: VPN IP rotated → {NewIp}", _label, newIp ?? "(unbekannt)");
            await WaitForProxyReadyAsync(ct);
            return newIp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("{Label}: VPN rotation failed (non-critical): {Error}", _label, ex.Message);
            return null;
        }
        finally
        {
            if (needsRestart) await EnsureVpnRunningAsync(client, statusUrl);
        }
    }

    private static async Task PutVpnStatusAsync(HttpClient client, string statusUrl, string json, CancellationToken ct)
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
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < maxAttempts) { }
        }
    }

    private async Task EnsureVpnRunningAsync(HttpClient client, string statusUrl)
    {
        try
        {
            _logger.LogWarning("{Label}: rotation incomplete → forcing VPN restart", _label);
            await PutVpnStatusAsync(client, statusUrl, """{"status":"running"}""", CancellationToken.None);
        }
        catch (Exception ex) { _logger.LogError(ex, "{Label}: VPN recovery restart failed", _label); }
    }

    private async Task<string?> PollPublicIpAsync(HttpClient client, CancellationToken ct)
    {
        for (int attempt = 0; attempt < PublicIpPollAttempts; attempt++)
        {
            try
            {
                await Task.Delay(PublicIpPollDelayMs, ct);
                var json = await client.GetStringAsync($"{_controlUrl}/v1/publicip/ip", ct);
                var ip = VpnRotationService.ParsePublicIp(json);
                if (!string.IsNullOrWhiteSpace(ip)) return ip;
            }
            catch (Exception ex) { _logger.LogDebug("{Label}: publicip attempt {A} failed: {E}", _label, attempt + 1, ex.Message); }
        }
        return null;
    }

    private async Task WaitForProxyReadyAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_proxyProbeUrl) || _probeClient is null) return;
        for (int attempt = 0; attempt < ProxyReadyPollAttempts; attempt++)
        {
            int status = 0;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ProxyProbeTimeoutMs);
                using var req = new HttpRequestMessage(HttpMethod.Head, _proxyProbeUrl);
                using var resp = await _probeClient.SendAsync(req, timeoutCts.Token);
                status = (int)resp.StatusCode;
            }
            catch (Exception ex) { _logger.LogDebug("{Label}: proxy probe {A} failed: {E}", _label, attempt + 1, ex.Message); }

            if (VpnRotationService.IsProxyReady(status))
            {
                _logger.LogInformation("{Label}: proxy ready after rotation (status {S}, attempt {A})", _label, status, attempt + 1);
                return;
            }
            await Task.Delay(ProxyReadyPollDelayMs, ct);
        }
        _logger.LogWarning("{Label}: proxy not confirmed ready after {N} attempts — proceeding", _label, ProxyReadyPollAttempts);
    }
}
