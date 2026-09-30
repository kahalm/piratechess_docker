namespace PirateChess.Api.Services;

/// <summary>
/// Satzteile, nach denen der log-watcher in piratechess-Logs sucht. Sie stehen dort als
/// <c>warn_spike_ignore</c> (config.example.yaml, Target piratechess; Prod-Konfig gleich): Warnungen, die
/// by design auftreten und nicht als Warnungs-Spitze zählen sollen. Formuliert jemand die Log-Templates
/// um, zählen sie still wieder mit und lösen Fehlalarme aus — darum bauen die Templates auf diesen
/// Konstanten auf, und LogWatcherContractTests pinnt sie wörtlich.
/// </summary>
public static class LogWatcherContract
{
    /// <summary>softFail + Retry bei wackeligem VPN-Exit (ChessableHttpService).</summary>
    public const string CurlExited = "curl exited with code";

    /// <summary>Kumulativer Bad-Phase-Zähler je VPN-IP (VpnIpHealth).</summary>
    public const string IpRepeatedlyBad = "WIEDERHOLT SCHLECHT";

    /// <summary>Toleranz-Diagnose des Kurs-Parsers: eine kaputte Linie/ein Kapitel wird übersprungen.
    /// Die Summenzeile („mit N übersprungenen Linien/Kapiteln") passt bewusst NICHT.</summary>
    public const string ParserSkipped = "übersprang eine Linie/Kapitel";

    /// <summary>Alle Einträge in der Reihenfolge der log-watcher-Beispielkonfiguration.</summary>
    public static IReadOnlyList<string> WarnSpikeIgnore { get; } = [CurlExited, IpRepeatedlyBad, ParserSkipped];
}
