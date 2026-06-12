using System;
using System.Text;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class ChessableJwtTests
{
    // Baut ein JWT mit gegebenem Payload-JSON (Signatur egal — wird nicht geprüft).
    private static string Jwt(string payloadJson)
    {
        string b64url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = b64url(Encoding.UTF8.GetBytes(payloadJson));
        return $"eyJhbGciOiJIUzI1NiJ9.{payload}.signaturehere";
    }

    [Fact]
    public void TryExtractUname_ValidToken_ReturnsUname()
    {
        var jwt = Jwt("""{"iat":1,"user":{"uid":790927,"uname":"kahalm","email":"x@y.com"}}""");
        Assert.Equal("kahalm", ChessableJwt.TryExtractUname(jwt));
    }

    [Fact]
    public void TryExtractUname_StripsBearerPrefix()
    {
        var jwt = "Bearer " + Jwt("""{"user":{"uname":"alice"}}""");
        Assert.Equal("alice", ChessableJwt.TryExtractUname(jwt));
    }

    [Fact]
    public void TryExtractUname_NoUnameClaim_ReturnsNull()
    {
        Assert.Null(ChessableJwt.TryExtractUname(Jwt("""{"user":{"uid":1}}""")));
    }

    [Fact]
    public void TryExtractUname_EmptyUname_ReturnsNull()
    {
        Assert.Null(ChessableJwt.TryExtractUname(Jwt("""{"user":{"uname":""}}""")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notajwt")]
    [InlineData("only.twoparts")]
    public void TryExtractUname_InvalidInput_ReturnsNull(string? token)
    {
        Assert.Null(ChessableJwt.TryExtractUname(token));
    }
}
