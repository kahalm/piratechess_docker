using System.Text;
using piratechess_lib;

namespace PirateChess.Api.Tests;

public class JwtHelperTests
{
    // Base64URL ohne Padding kodieren (so wie echte JWTs) — testet zugleich das Padding-Auffüllen.
    private static string B64Url(string json)
    {
        var b = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return b.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string MakeJwt(string payloadJson) => $"header.{B64Url(payloadJson)}.signature";

    [Fact]
    public void ExtractUidFromToken_ReadsNestedUid()
    {
        var jwt = MakeJwt("{\"user\":{\"uid\":4242},\"exp\":9999999999}");
        Assert.Equal(4242, JwtHelper.ExtractUidFromToken(jwt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractUidFromToken_ThrowsOnEmpty(string token)
    {
        Assert.Throws<ArgumentException>(() => JwtHelper.ExtractUidFromToken(token));
    }

    [Fact]
    public void ExtractUidFromToken_ThrowsOnMalformedToken()
    {
        Assert.Throws<ArgumentException>(() => JwtHelper.ExtractUidFromToken("onlyonepart"));
    }

    [Fact]
    public void ExtractUidFromToken_ThrowsWhenUidMissing()
    {
        var jwt = MakeJwt("{\"foo\":1}");
        Assert.Throws<InvalidOperationException>(() => JwtHelper.ExtractUidFromToken(jwt));
    }

    [Fact]
    public void GetExpiration_ParsesExpClaim()
    {
        var jwt = MakeJwt("{\"exp\":1700000000}");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), JwtHelper.GetExpiration(jwt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("onlyonepart")]
    public void GetExpiration_ReturnsNullForInvalidToken(string token)
    {
        Assert.Null(JwtHelper.GetExpiration(token));
    }

    [Fact]
    public void GetExpiration_ReturnsNullWhenNoExpClaim()
    {
        Assert.Null(JwtHelper.GetExpiration(MakeJwt("{\"user\":{\"uid\":1}}")));
    }

    [Fact]
    public void IsExpired_TrueForPast_FalseForFuture()
    {
        var past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        Assert.True(JwtHelper.IsExpired(MakeJwt($"{{\"exp\":{past}}}")));
        Assert.False(JwtHelper.IsExpired(MakeJwt($"{{\"exp\":{future}}}")));
    }

    [Fact]
    public void IsExpired_FalseWhenNoExpClaim()
    {
        // Ohne exp ist nicht feststellbar, dass abgelaufen → false (nicht abgelaufen).
        Assert.False(JwtHelper.IsExpired(MakeJwt("{\"user\":{\"uid\":1}}")));
    }
}
