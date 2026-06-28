namespace PirateChess.Api.Services;

/// <summary>
/// Verwaltet einen Pool aus 1..n gluetun-VPN-Tunneln (je eigener HTTP-Proxy + IP-Rotation).
/// Requests werden round-robin über die Tunnel verteilt; pro Request liefert <see cref="AcquireAsync"/>
/// ein <see cref="VpnLease"/> mit dem zu nutzenden Proxy. Mit nur einem Tunnel = bisheriges Verhalten.
/// </summary>
public interface IVpnRotationService
{
    /// <summary>Wählt den nächsten Tunnel (round-robin), zählt ihn mit / rotiert ihn bei Bedarf
    /// (drain-aware) und liefert ein Lease: <see cref="VpnLease.ProxyUrl"/> für den Request, und
    /// <see cref="VpnLease.Dispose"/> (im finally) meldet den Request als fertig.</summary>
    Task<VpnLease> AcquireAsync(CancellationToken ct = default);

    /// <summary>Erzwingt sofort eine Rotation ALLER Tunnel (manueller Trigger / Test); liefert die
    /// erste neue Public-IP (oder null).</summary>
    Task<string?> RotateNowAsync(CancellationToken ct = default);

    /// <summary>Aktuelle Public-IP des ersten Tunnels (best-effort).</summary>
    Task<string?> GetPublicIpAsync(CancellationToken ct = default);
}

/// <summary>Leiht einen Tunnel für genau einen Request. <see cref="ProxyUrl"/> ist der zu nutzende
/// Proxy (oder null = direkt). <see cref="Dispose"/> meldet den Request beim Tunnel als beendet
/// (genau einmal) — im <c>finally</c> aufrufen, damit die drain-aware Rotation korrekt zählt.</summary>
public sealed class VpnLease : IDisposable
{
    public string? ProxyUrl { get; }
    private readonly Action _onComplete;
    private bool _completed;

    public VpnLease(string? proxyUrl, Action onComplete)
    {
        ProxyUrl = proxyUrl;
        _onComplete = onComplete;
    }

    public void Dispose()
    {
        if (_completed) return;
        _completed = true;
        _onComplete();
    }
}
