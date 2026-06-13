namespace PirateChess.Api.Services;

/// <summary>
/// Wird geworfen, wenn ein Chessable-Request am gluetun-HTTP-Proxy (:8888) mit
/// „CONNECT tunnel failed, response 503" scheitert — typischerweise, weil die
/// VPN-IP gerade rotiert/reconnectet. Transient: ein Retry nach kurzer Pause
/// ist sinnvoll.
/// </summary>
public class ProxyTunnelException : Exception
{
    public ProxyTunnelException(string message) : base(message) { }
}
