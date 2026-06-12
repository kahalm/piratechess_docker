namespace PirateChess.Api.Services;

/// <summary>
/// Dreht die öffentliche (gluetun-)Exit-IP über den gluetun-Control-Server.
/// Spiegelt das Rotations-Muster des ChessResults-Crawlers, der sich denselben
/// gluetun teilt. Anders als der Crawler läuft piratechess-api NICHT im
/// gluetun-Namespace, sondern erreicht den Control-Server über das Bridge-Netz
/// (<c>Gluetun:ControlUrl</c>, z.B. http://gluetun:8000).
/// </summary>
public interface IVpnRotationService
{
    /// <summary>
    /// Zählt jeden Chessable-Request mit und rotiert die IP nach jeweils
    /// <c>Vpn:RotateAfterRequests</c> Aufrufen. No-op, wenn die Rotation
    /// deaktiviert ist (keine <c>Gluetun:ControlUrl</c> konfiguriert).
    /// Aufruf VOR dem eigentlichen Request → die Rotation passiert immer
    /// zwischen zwei Requests, nie mitten in einem.
    /// </summary>
    Task MaybeRotateAsync(CancellationToken ct = default);

    /// <summary>
    /// Erzwingt sofort eine Rotation (manueller Trigger / Test) und setzt den
    /// Request-Zähler zurück. Gibt die neue Public-IP zurück (oder null, wenn
    /// nicht ermittelbar bzw. keine ControlUrl konfiguriert).
    /// </summary>
    Task<string?> RotateNowAsync(CancellationToken ct = default);

    /// <summary>Aktuelle Public-IP laut gluetun-Control-Server (best-effort).</summary>
    Task<string?> GetPublicIpAsync(CancellationToken ct = default);
}
