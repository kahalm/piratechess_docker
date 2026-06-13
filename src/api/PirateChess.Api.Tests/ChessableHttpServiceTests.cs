using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class ChessableHttpServiceTests
{
    // --- Transienter Proxy-Ausfall (Fix: gluetun :8888 liefert beim VPN-Reconnect 503) ---

    [Fact]
    public void IsTransientProxyFailure_Curl56Tunnel503_ReturnsTrue()
    {
        // Exakt der beobachtete curl-Fehler aus der ChessableRawResponses-Tabelle
        const string stderr = "curl: (56) CONNECT tunnel failed, response 503";
        Assert.True(ChessableHttpService.IsTransientProxyFailure(56, stderr));
    }

    [Theory]
    [InlineData("Received HTTP code 503 from proxy after CONNECT")]
    [InlineData("response 503")]
    [InlineData("CONNECT TUNNEL FAILED, RESPONSE 503")] // Case-insensitiv
    public void IsTransientProxyFailure_Tunnel503Variants_ReturnsTrue(string stderr)
    {
        Assert.True(ChessableHttpService.IsTransientProxyFailure(56, stderr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsTransientProxyFailure_NoError_ReturnsFalse(string? stderr)
    {
        Assert.False(ChessableHttpService.IsTransientProxyFailure(0, stderr));
    }

    [Theory]
    [InlineData(6, "curl: (6) Could not resolve host: www.chessable.com")]
    [InlineData(28, "curl: (28) Operation timed out")]
    [InlineData(0, "Received HTTP code 403 from proxy after CONNECT")] // echtes 403 ≠ transient
    public void IsTransientProxyFailure_NonTunnelError_ReturnsFalse(int exitCode, string stderr)
    {
        Assert.False(ChessableHttpService.IsTransientProxyFailure(exitCode, stderr));
    }
}
