namespace PirateChess.Api.Services;

/// <summary>
/// Buchführung pro VPN-Ausgangs-IP über ALLE Tunnel/Rotationen hinweg: wie viele Requests liefen
/// über die IP und wie viele waren blockiert/getimeoutet. Eine „Stint" = die Lebensdauer einer IP
/// zwischen zwei Rotationen; sie wird per <see cref="RecordStint"/> gemeldet. Eine IP, die mehrfach
/// mit hoher Block-Rate auffällt, wird als „wiederholt schlecht" geloggt (WARN). <see cref="Snapshot"/>
/// liefert die Tabelle für eine Ad-hoc-Auswertung (Debug-Endpoint /direct/debug/ip-health).
/// </summary>
public sealed class VpnIpHealth
{
    public sealed record IpStat(string Ip, long Requests, long Blocks, int Stints, int BadStints, DateTime LastSeenUtc)
    {
        public double BlockRate => Requests > 0 ? (double)Blocks / Requests : 0;
    }

    private sealed class Counter
    {
        public long Requests;
        public long Blocks;
        public int Stints;
        public int BadStints;
        public DateTime LastSeenUtc;
    }

    private readonly Dictionary<string, Counter> _byIp = new();
    private readonly object _lock = new();
    private readonly ILogger<VpnIpHealth> _logger;
    private readonly double _badStintRate;

    public VpnIpHealth(IConfiguration cfg, ILogger<VpnIpHealth> logger)
    {
        _logger = logger;
        _badStintRate = Math.Clamp(cfg.GetValue("Vpn:BadStintRate", 0.4), 0.05, 1.0);
    }

    /// <summary>Meldet die abgeschlossene Lebensdauer einer IP: <paramref name="requests"/> Requests,
    /// davon <paramref name="blocks"/> blockiert. No-op ohne IP oder ohne Requests.</summary>
    public void RecordStint(string? ip, int requests, int blocks)
    {
        if (string.IsNullOrWhiteSpace(ip) || requests <= 0) return;

        var stintBad = (double)blocks / requests >= _badStintRate;
        long totReq, totBlk; int badStints, stints;
        lock (_lock)
        {
            if (!_byIp.TryGetValue(ip, out var c)) { c = new Counter(); _byIp[ip] = c; }
            c.Requests += requests;
            c.Blocks += blocks;
            c.Stints++;
            if (stintBad) c.BadStints++;
            c.LastSeenUtc = DateTime.UtcNow;
            totReq = c.Requests; totBlk = c.Blocks; badStints = c.BadStints; stints = c.Stints;
        }

        // Strukturiert (ip/requests/blocked als Felder) → in Kibana je IP gruppier-/aggregierbar.
        _logger.LogInformation(
            "VPN-IP-Stint ip={Ip} requests={Req} blocked={Blk} rate={Rate:P0}; kumuliert {TotReq}/{TotBlk}, {Bad}/{Stints} schlechte Phasen",
            ip, requests, blocks, (double)blocks / requests, totReq, totBlk, badStints, stints);

        if (stintBad && badStints >= 2)
            _logger.LogWarning(
                "VPN-IP {Ip} WIEDERHOLT SCHLECHT: {Bad} schlechte Phasen (von {Stints}), gesamt {TotReq} Requests / {TotBlk} blockiert ({Rate:P0})",
                ip, badStints, stints, totReq, totBlk, (double)totBlk / totReq);
    }

    /// <summary>Aktuelle Per-IP-Statistik (schlechteste zuerst) für eine Ad-hoc-Auswertung.</summary>
    public IReadOnlyList<IpStat> Snapshot()
    {
        lock (_lock)
        {
            return _byIp
                .Select(kv => new IpStat(kv.Key, kv.Value.Requests, kv.Value.Blocks, kv.Value.Stints, kv.Value.BadStints, kv.Value.LastSeenUtc))
                .OrderByDescending(s => s.BadStints)
                .ThenByDescending(s => s.Blocks)
                .ToList();
        }
    }
}
