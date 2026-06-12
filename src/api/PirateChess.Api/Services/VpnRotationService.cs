using System.Text;
using System.Text.Json;

namespace PirateChess.Api.Services;

/// <inheritdoc cref="IVpnRotationService"/>
public class VpnRotationService : IVpnRotationService
{
    /// <summary>Name des un-proxied HttpClients (Control-Calls dürfen NICHT durch :8888 laufen).</summary>
    public const string ClientName = "GluetunControl";

    private const int RestartPauseMs = 3000;   // wie der Crawler: kurz warten zwischen stop/running
    private const int PublicIpPollAttempts = 5; // gluetun braucht nach Reconnect kurz für die neue IP
    private const int PublicIpPollDelayMs = 1000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VpnRotationService> _logger;
    private readonly string? _controlUrl;
    private readonly int _rotateAfter;
    private readonly bool _enabled;

    // Serialisiert Zähler UND Rotation: keine zwei Rotationen gleichzeitig, und
    // kein Request startet, während rotiert wird (Aufrufer awaiten MaybeRotateAsync).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _requestCount;

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
                await RotateInternalAsync(ct);
            }
        }
        finally
        {
            _gate.Release();
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

        try
        {
            _logger.LogInformation("Rotating VPN IP...");
            using (var stop = new StringContent("""{"status":"stopped"}""", Encoding.UTF8, "application/json"))
                (await client.PutAsync(statusUrl, stop, ct)).EnsureSuccessStatusCode();

            await Task.Delay(RestartPauseMs, ct);

            using (var start = new StringContent("""{"status":"running"}""", Encoding.UTF8, "application/json"))
                (await client.PutAsync(statusUrl, start, ct)).EnsureSuccessStatusCode();

            var newIp = await PollPublicIpAsync(client, ct);
            if (newIp is not null)
                _logger.LogInformation("VPN IP rotated → {NewIp}", newIp);
            else
                _logger.LogInformation("VPN IP rotated (neue IP nicht ermittelbar)");
            return newIp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VPN rotation failed (non-critical)");
            return null;
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
                _logger.LogDebug(ex, "publicip query attempt {Attempt} failed", attempt + 1);
            }
        }
        return null;
    }

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
