namespace PirateChess.Api.Services;

/// <summary>
/// Buchführung pro VPN-Ausgangs-IP über ALLE Tunnel/Rotationen hinweg: wie viele Requests liefen
/// über die IP und wie viele waren blockiert/getimeoutet. Eine „Stint" = die Lebensdauer einer IP
/// zwischen zwei Rotationen; sie wird per <see cref="RecordStint"/> gemeldet.
///
/// „Wiederholt schlecht" wird an der KUMULATIVEN Per-IP-Block-Rate festgemacht (über genug
/// Requests), NICHT an gezählten Mini-Phasen — sonst flaggt ein einzelner Block in einem kurzen
/// Stint (1 von 2 Requests = 50 %) eine in Wahrheit gesunde IP. Eine Phase zählt deshalb nur als
/// „schlecht", wenn sie groß genug war (<c>Vpn:BadStintMinRequests</c>), und der WARN feuert erst,
/// wenn die IP über <c>Vpn:BadIpMinRequests</c> hinweg eine Block-Rate ≥ <c>Vpn:BadIpBlockRate</c>
/// hält (gedrosselt, nicht pro Stint). <see cref="Snapshot"/> liefert die Tabelle für eine
/// Ad-hoc-Auswertung (Debug-Endpoint /direct/debug/ip-health).
/// </summary>
public sealed class VpnIpHealth
{
    public sealed record IpStat(string Ip, long Requests, long Blocks, int Stints, int BadStints, bool RecurringBad, DateTime LastSeenUtc)
    {
        public double BlockRate => Requests > 0 ? (double)Blocks / Requests : 0;
    }

    private sealed class Counter
    {
        public long Requests;
        public long Blocks;
        public int Stints;
        public int BadStints;
        public int LastWarnedStint;
        public DateTime LastSeenUtc;
    }

    // Re-WARN frühestens nach so vielen weiteren Stints, sobald eine IP einmal geflaggt wurde.
    private const int ReWarnEveryStints = 20;

    private readonly Dictionary<string, Counter> _byIp = new();
    private readonly object _lock = new();
    private readonly ILogger<VpnIpHealth> _logger;
    private readonly double _badStintRate;
    private readonly int _badStintMinRequests;
    private readonly int _badIpMinRequests;
    private readonly double _badIpBlockRate;
    private readonly int _maxEntries;

    public VpnIpHealth(IConfiguration cfg, ILogger<VpnIpHealth> logger)
    {
        _logger = logger;
        _badStintRate = Math.Clamp(cfg.GetValue("Vpn:BadStintRate", 0.4), 0.05, 1.0);
        _badStintMinRequests = Math.Clamp(cfg.GetValue("Vpn:BadStintMinRequests", 5), 1, 1000);
        _badIpMinRequests = Math.Clamp(cfg.GetValue("Vpn:BadIpMinRequests", 50), 1, 100000);
        _badIpBlockRate = Math.Clamp(cfg.GetValue("Vpn:BadIpBlockRate", 0.15), 0.01, 1.0);
        // Obergrenze gegen unbegrenztes Wachstum im langlebigen Prozess: bei aggressiver Rotation über
        // einen großen Provider-IP-Pool sammeln sich sonst über Tage beliebig viele Einträge an.
        _maxEntries = Math.Clamp(cfg.GetValue("Vpn:IpHealthMaxEntries", 1000), 50, 100000);
    }

    /// <summary>Meldet die abgeschlossene Lebensdauer einer IP: <paramref name="requests"/> Requests,
    /// davon <paramref name="blocks"/> blockiert. No-op ohne IP oder ohne Requests.</summary>
    public void RecordStint(string? ip, int requests, int blocks)
    {
        if (string.IsNullOrWhiteSpace(ip) || requests <= 0) return;

        // Eine Phase zählt nur als „schlecht", wenn sie statistisch belastbar war: genug Requests
        // UND hohe Block-Rate. Ein einzelner Block in 1–2 Requests ist Rauschen, kein Indiz.
        var stintBad = requests >= _badStintMinRequests && (double)blocks / requests >= _badStintRate;

        long totReq, totBlk; int badStints, stints; bool warn;
        lock (_lock)
        {
            if (!_byIp.TryGetValue(ip, out var c))
            {
                c = new Counter();
                _byIp[ip] = c;
                // Bei Neuzugang ggf. die am längsten nicht gesehenen Einträge ausmisten (Cap halten).
                if (_byIp.Count > _maxEntries) EvictOldest();
            }
            c.Requests += requests;
            c.Blocks += blocks;
            c.Stints++;
            if (stintBad) c.BadStints++;
            c.LastSeenUtc = DateTime.UtcNow;
            totReq = c.Requests; totBlk = c.Blocks; badStints = c.BadStints; stints = c.Stints;

            // „Wiederholt schlecht": die IP hält über genug Requests eine zu hohe Gesamt-Block-Rate.
            var recurringBad = totReq >= _badIpMinRequests && (double)totBlk / totReq >= _badIpBlockRate;
            warn = recurringBad && (c.LastWarnedStint == 0 || stints - c.LastWarnedStint >= ReWarnEveryStints);
            if (warn) c.LastWarnedStint = stints;
        }

        // Strukturiert (ip/requests/blocked als Felder) → in Kibana je IP gruppier-/aggregierbar.
        _logger.LogInformation(
            "VPN-IP-Stint ip={Ip} requests={Req} blocked={Blk} rate={Rate:P0}; kumuliert {TotReq}/{TotBlk}, {Bad}/{Stints} schlechte Phasen",
            ip, requests, blocks, (double)blocks / requests, totReq, totBlk, badStints, stints);

        if (warn)
            _logger.LogWarning(
                "VPN-IP {Ip} WIEDERHOLT SCHLECHT: Gesamt-Block-Rate {Rate:P0} über {TotReq} Requests ({TotBlk} blockiert, {Bad}/{Stints} schlechte Phasen)",
                ip, (double)totBlk / totReq, totReq, totBlk, badStints, stints);
    }

    /// <summary>Entfernt die am längsten nicht gesehenen Einträge, bis der Cap wieder eingehalten ist
    /// (10 % Headroom, damit nicht bei jedem Neuzugang erneut ausgemistet wird). Aufruf unter <c>_lock</c>.</summary>
    private void EvictOldest()
    {
        var target = _maxEntries - _maxEntries / 10;
        foreach (var key in _byIp.OrderBy(kv => kv.Value.LastSeenUtc).Select(kv => kv.Key).Take(_byIp.Count - target).ToList())
            _byIp.Remove(key);
    }

    /// <summary>Aktuelle Per-IP-Statistik (höchste Block-Rate zuerst) für eine Ad-hoc-Auswertung.</summary>
    public IReadOnlyList<IpStat> Snapshot()
    {
        lock (_lock)
        {
            return _byIp
                .Select(kv =>
                {
                    var c = kv.Value;
                    var recurringBad = c.Requests >= _badIpMinRequests && (double)c.Blocks / c.Requests >= _badIpBlockRate;
                    return new IpStat(kv.Key, c.Requests, c.Blocks, c.Stints, c.BadStints, recurringBad, c.LastSeenUtc);
                })
                .OrderByDescending(s => s.RecurringBad)
                .ThenByDescending(s => s.BlockRate)
                .ThenByDescending(s => s.Blocks)
                .ToList();
        }
    }
}
