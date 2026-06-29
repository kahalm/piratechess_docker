using System.Text.Json;

namespace PirateChess.Api.Services;

/// <summary>
/// Pool aus einem oder mehreren <see cref="VpnTunnel"/>n. Es ist immer GENAU EIN Tunnel aktiv;
/// Requests laufen sticky über ihn, bis er sein Request-Budget erreicht ODER einen IP-Soft-Block
/// meldet. Dann rotiert er im Hintergrund (drain-aware) und der nächste, bereits ausgeruhte Tunnel
/// wird aktiv (Ping-Pong: während A liefert, rotiert B auf eine frische IP und ist beim Wechsel fertig).
///
/// Konfiguration: <c>Chessable:ProxyUrls</c> + <c>Gluetun:ControlUrls</c> (komma-getrennt, paarweise
/// per Index). Fallback auf die Einzelwerte <c>Chessable:ProxyUrl</c>/<c>Gluetun:ControlUrl</c> →
/// genau 1 Tunnel = bisheriges Verhalten.
/// </summary>
public class VpnRotationService : IVpnRotationService
{
    /// <summary>Name des un-proxied HttpClients (Control-Calls dürfen NICHT durch den Proxy laufen).</summary>
    public const string ClientName = "GluetunControl";

    private readonly List<VpnTunnel> _tunnels;
    private readonly object _activeLock = new();
    private int _active;   // Index des aktuell aktiven Tunnels (sticky)

    public VpnRotationService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<VpnRotationService> logger)
    {
        var proxyUrls = ParseList(configuration["Chessable:ProxyUrls"]) ?? SingleOrEmpty(configuration["Chessable:ProxyUrl"]);
        var controlUrls = ParseList(configuration["Gluetun:ControlUrls"]) ?? SingleOrEmpty(configuration["Gluetun:ControlUrl"]);

        var count = Math.Max(Math.Max(proxyUrls.Count, controlUrls.Count), 1);
        _tunnels = new List<VpnTunnel>(count);
        for (int i = 0; i < count; i++)
        {
            var proxy = i < proxyUrls.Count ? proxyUrls[i] : null;
            var control = i < controlUrls.Count ? controlUrls[i] : null;
            _tunnels.Add(new VpnTunnel(proxy, control, httpClientFactory, configuration, logger, i + 1, count));
        }
        logger.LogInformation("VPN-Tunnel-Pool: {Count} Tunnel", _tunnels.Count);
    }

    /// <summary>Liefert ein Lease auf den AKTIVEN Tunnel (sticky). Ist er gerade am Rotieren, rückt
    /// die Suche auf den nächsten bereiten Tunnel und macht ihn aktiv. Erreicht der aktive Tunnel
    /// dabei sein Budget, stößt <see cref="VpnTunnel.TryAcquire"/> die Hintergrund-Rotation an und der
    /// nächste Acquire wechselt automatisch. <see cref="VpnLease.ReportBlocked"/> retired ihn sofort.
    /// Nur falls (sehr selten) ALLE rotieren: kurz warten und erneut versuchen.</summary>
    public async Task<VpnLease> AcquireAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (_activeLock)
            {
                // Beim aktiven Tunnel beginnen, dann der Reihe nach — der erste bereite gewinnt
                // und wird zum aktiven (rotierende werden übersprungen).
                for (var k = 0; k < _tunnels.Count; k++)
                {
                    var idx = (_active + k) % _tunnels.Count;
                    var tunnel = _tunnels[idx];
                    if (tunnel.TryAcquire())
                    {
                        _active = idx;
                        return new VpnLease(tunnel.ProxyUrl, tunnel.RequestCompleted, () => RetireAndAdvance(idx));
                    }
                }
            }
            await Task.Delay(50, ct);
        }
    }

    /// <summary>Retired den (geblockten) Tunnel <paramref name="idx"/> sofort: Hintergrund-Rotation
    /// auf eine frische IP + aktiven Zeiger auf den nächsten Tunnel rücken, damit der nächste Acquire
    /// nicht erneut die verbrannte IP zieht.</summary>
    private void RetireAndAdvance(int idx)
    {
        lock (_activeLock)
        {
            _tunnels[idx].RetireNow();
            if (_active == idx)
                _active = (idx + 1) % _tunnels.Count;
        }
    }

    /// <summary>Rotiert ALLE Tunnel sofort (manueller Trigger); liefert die erste neue IP.</summary>
    public async Task<string?> RotateNowAsync(CancellationToken ct = default)
    {
        var ips = await Task.WhenAll(_tunnels.Select(t => t.RotateNowAsync(ct)));
        return ips.FirstOrDefault(ip => ip is not null);
    }

    /// <summary>Public-IP des ersten Tunnels (best-effort).</summary>
    public Task<string?> GetPublicIpAsync(CancellationToken ct = default) => _tunnels[0].GetPublicIpAsync(ct);

    private static List<string>? ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var list = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return list.Count == 0 ? null : list;
    }

    private static List<string> SingleOrEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? new List<string>() : new List<string> { value.Trim() };

    public static bool IsProxyReady(int httpStatusCode) => httpStatusCode > 0 && httpStatusCode != 503;

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
        catch (JsonException) { }
        return null;
    }
}
