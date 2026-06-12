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
}
