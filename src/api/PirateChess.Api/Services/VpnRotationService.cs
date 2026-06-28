using System.Text.Json;

namespace PirateChess.Api.Services;

/// <summary>
/// Pool aus einem oder mehreren <see cref="VpnTunnel"/>n. Verteilt Chessable-Requests round-robin
/// über die Tunnel (jeder = eigener gluetun-Proxy + eigene IP-Rotation), damit während ein Tunnel
/// rotiert ein anderer weiterliefern kann (→ höherer Durchsatz, Rotation aus dem kritischen Pfad).
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
    private int _rr = -1;

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
            _tunnels.Add(new VpnTunnel(proxy, control, httpClientFactory, configuration, logger, i + 1));
        }
        logger.LogInformation("VPN-Tunnel-Pool: {Count} Tunnel", _tunnels.Count);
    }

    /// <summary>Wählt den nächsten Tunnel (round-robin), zählt/rotiert ihn und liefert ein Lease,
    /// dessen Proxy für den Request zu nutzen ist; <c>Dispose</c> meldet den Request als fertig.</summary>
    public async Task<VpnLease> AcquireAsync(CancellationToken ct = default)
    {
        var tunnel = _tunnels[(int)((uint)Interlocked.Increment(ref _rr) % (uint)_tunnels.Count)];
        await tunnel.MaybeRotateAsync(ct);
        return new VpnLease(tunnel.ProxyUrl, tunnel.RequestCompleted);
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
