using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class RawResponseRedactionTests
{
    [Fact]
    public void RedactForStorage_LoginBody_RedactsJwt()
    {
        var body = "{\"jwt\":\"eyJhbGciOi.SECRET.sig\",\"user\":{\"uid\":12345}}";
        var redacted = ChessableHttpService.RedactForStorage("login", body);

        Assert.DoesNotContain("eyJhbGciOi.SECRET.sig", redacted);
        Assert.Contains("\"jwt\":\"[redacted]\"", redacted);
        Assert.Contains("12345", redacted);   // restlicher Body bleibt erhalten
    }

    [Fact]
    public void RedactForStorage_LoginBody_TolerantToWhitespace()
    {
        var body = "{ \"jwt\" : \"abc.def.ghi\" }";
        var redacted = ChessableHttpService.RedactForStorage("login", body);
        Assert.DoesNotContain("abc.def.ghi", redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Fact]
    public void RedactForStorage_NonLoginEndpoint_Unchanged()
    {
        var body = "{\"list\":{\"data\":[1,2,3]}}";
        Assert.Equal(body, ChessableHttpService.RedactForStorage("getList", body));
    }

    [Fact]
    public void RedactForStorage_EmptyBody_Unchanged()
    {
        Assert.Equal("", ChessableHttpService.RedactForStorage("login", ""));
    }
}
