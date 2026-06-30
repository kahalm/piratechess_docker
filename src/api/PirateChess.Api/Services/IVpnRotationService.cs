namespace PirateChess.Api.Services;

/// <summary>
/// Verwaltet einen Pool aus 1..n gluetun-VPN-Tunneln (je eigener HTTP-Proxy + IP-Rotation).
/// Es ist immer GENAU EIN Tunnel aktiv; Requests laufen sticky über ihn, bis er sein Request-Budget
/// erreicht ODER einen IP-Soft-Block meldet (<see cref="VpnLease.ReportBlocked"/>). Dann wird er im
/// Hintergrund rotiert (drain-aware) und der nächste, bereits ausgeruhte Tunnel wird aktiv
/// (Ping-Pong). Mit nur einem Tunnel = bisheriges Verhalten.
/// </summary>
public interface IVpnRotationService
{
    /// <summary>Liefert ein Lease auf den aktiven Tunnel (überspringt gerade rotierende). <see cref="VpnLease.ProxyUrl"/>
    /// ist der zu nutzende Proxy; <see cref="VpnLease.Dispose"/> (im finally) meldet den Request als fertig;
    /// <see cref="VpnLease.ReportBlocked"/> retired die aktive IP sofort (Hintergrund-Rotation + Wechsel).</summary>
    Task<VpnLease> AcquireAsync(CancellationToken ct = default);

    /// <summary>Liefert ein Lease auf GENAU den Tunnel <paramref name="index"/> (0-basiert), unabhängig vom
    /// sticky round-robin und ohne den aktiven Zeiger zu verschieben — für einen gezielten Test „über genau
    /// diesen VPN". Ein Cooldown wird ignoriert (manueller Trigger), eine laufende Rotation aber abgewartet.
    /// Wirft <see cref="ArgumentOutOfRangeException"/> bei ungültigem Index.</summary>
    Task<VpnLease> AcquireSpecificAsync(int index, CancellationToken ct = default);

    /// <summary>Anzahl der Tunnel im Pool (gültige Pin-Indizes: 0..Count-1).</summary>
    int TunnelCount { get; }

    /// <summary>Schnappschuss aller Tunnel (Index, Proxy, Status) — für die Tunnel-Auswahl beim Pin-Test.</summary>
    IReadOnlyList<VpnTunnelStatus> DescribeTunnels();

    /// <summary>Aktuelle Public-(Exit-)IP GENAU des Tunnels <paramref name="index"/> über dessen Control-Server
    /// (best-effort, null wenn nicht ermittelbar). Wirft <see cref="ArgumentOutOfRangeException"/> bei ungültigem Index.</summary>
    Task<string?> GetTunnelPublicIpAsync(int index, CancellationToken ct = default);

    /// <summary>Erzwingt sofort eine Rotation ALLER Tunnel (manueller Trigger / Test); liefert die
    /// erste neue Public-IP (oder null).</summary>
    Task<string?> RotateNowAsync(CancellationToken ct = default);

    /// <summary>Aktuelle Public-IP des ersten Tunnels (best-effort).</summary>
    Task<string?> GetPublicIpAsync(CancellationToken ct = default);
}

/// <summary>Diagnose-Schnappschuss eines Tunnels im Pool (für die Tunnel-Auswahl beim Pin-Test).
/// <paramref name="Index"/> ist 0-basiert und der für <see cref="IVpnRotationService.AcquireSpecificAsync"/>
/// erwartete Pin-Wert; <paramref name="ProxyUrl"/> unterscheidet die Anbieter (z. B. gluetun vs. gluetun-pia).</summary>
public record VpnTunnelStatus(int Index, string? ProxyUrl, string Label, bool Active, bool Rotating, bool CoolingDown);

/// <summary>Leiht einen Tunnel für genau einen Request. <see cref="ProxyUrl"/> ist der zu nutzende
/// Proxy (oder null = direkt). <see cref="Dispose"/> meldet den Request beim Tunnel als beendet
/// (genau einmal) — im <c>finally</c> aufrufen, damit die drain-aware Rotation korrekt zählt.
/// <see cref="ReportBlocked"/> signalisiert einen IP-Soft-Block (leere <c>{}</c>-Antwort) → die
/// aktive IP wird sofort retired (Hintergrund-Rotation) und der Pool wechselt auf den nächsten
/// Tunnel; der Aufrufer muss dann nur kurz (statt 30 s) zurückfallen und neu acquiren.</summary>
public sealed class VpnLease : IDisposable
{
    public string? ProxyUrl { get; }
    private readonly Action<bool> _onComplete;
    private readonly Action _onBlocked;
    private bool _completed;
    private bool _blocked;

    /// <param name="onComplete">Beim Dispose aufgerufen; bool = ob dieser Request (IP-)blockiert war
    /// (für die Tunnel-Health-/Cooldown-Statistik).</param>
    public VpnLease(string? proxyUrl, Action<bool> onComplete, Action? onBlocked = null)
    {
        ProxyUrl = proxyUrl;
        _onComplete = onComplete;
        _onBlocked = onBlocked ?? (static () => { });
    }

    /// <summary>Meldet diesen Request als (IP-)blockiert → der Tunnel retired/rotiert sofort.
    /// Idempotent (höchstens einmal wirksam pro Lease).</summary>
    public void ReportBlocked()
    {
        if (_blocked) return;
        _blocked = true;
        _onBlocked();
    }

    public void Dispose()
    {
        if (_completed) return;
        _completed = true;
        _onComplete(_blocked);
    }
}
