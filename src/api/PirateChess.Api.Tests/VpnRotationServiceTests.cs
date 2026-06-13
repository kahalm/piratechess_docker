using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class VpnRotationServiceTests
{
    [Fact]
    public void ParsePublicIp_ValidResponse_ReturnsIp()
    {
        // gluetun /v1/publicip/ip liefert ein Objekt mit public_ip + Geo-Feldern
        var json = """
            {"public_ip":"141.98.102.179","region":"Hesse","country":"Germany","city":"Frankfurt am Main"}
            """;

        Assert.Equal("141.98.102.179", VpnRotationService.ParsePublicIp(json));
    }

    [Fact]
    public void ParsePublicIp_MissingField_ReturnsNull()
    {
        Assert.Null(VpnRotationService.ParsePublicIp("""{"country":"Germany"}"""));
    }

    [Fact]
    public void ParsePublicIp_EmptyIp_ReturnsNull()
    {
        Assert.Null(VpnRotationService.ParsePublicIp("""{"public_ip":""}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"141.98.102.179\"")]
    public void ParsePublicIp_InvalidOrNonObject_ReturnsNull(string json)
    {
        Assert.Null(VpnRotationService.ParsePublicIp(json));
    }

    // --- Proxy-Readiness nach Rotation (Fix: gluetun :8888 liefert beim Reconnect kurz 503) ---

    [Fact]
    public void IsProxyReady_503_ReturnsFalse()
    {
        // gluetun lehnt den CONNECT-Tunnel während des Reconnects mit 503 ab → noch nicht bereit
        Assert.False(VpnRotationService.IsProxyReady(503));
    }

    [Theory]
    [InlineData(0)]    // Probe warf (Tunnel down / Timeout) → kein Statuscode
    [InlineData(-1)]
    public void IsProxyReady_NoResponse_ReturnsFalse(int status)
    {
        Assert.False(VpnRotationService.IsProxyReady(status));
    }

    [Theory]
    [InlineData(200)]  // Origin durch den Tunnel erreicht
    [InlineData(403)]  // Chessable blockt den simplen Probe-Client — Tunnel steht aber
    [InlineData(404)]
    [InlineData(405)]  // HEAD nicht erlaubt — Tunnel steht
    public void IsProxyReady_GotOriginResponse_ReturnsTrue(int status)
    {
        Assert.True(VpnRotationService.IsProxyReady(status));
    }
}
