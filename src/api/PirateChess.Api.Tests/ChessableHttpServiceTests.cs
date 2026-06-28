using System;
using System.Text;
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

    // --- Chessable-Fehler-Body trotz HTTP 200 (Fix: abgelaufener Bearer → „keine Kurse") ---

    [Fact]
    public void TryGetChessableErrorMessage_ExpiredToken_ReturnsHint()
    {
        // Exakt der beobachtete Body aus ChessableRawResponses bei abgelaufenem Bearer.
        const string body = "{\"error\":{\"message\":\"Expired token\"}}";
        var msg = ChessableHttpService.TryGetChessableErrorMessage(body);
        Assert.NotNull(msg);
        Assert.Contains("Expired token", msg);
        Assert.Contains("neu hinterlegen", msg); // Hinweis auf neuen Bearer
    }

    [Theory]
    [InlineData("{\"error\":\"Something went wrong\"}")]            // error als String
    [InlineData("{\"error\":{\"message\":\"Invalid request\"}}")]   // error.message ohne „token"
    public void TryGetChessableErrorMessage_GenericError_ReturnsMessage(string body)
    {
        var msg = ChessableHttpService.TryGetChessableErrorMessage(body);
        Assert.NotNull(msg);
        Assert.StartsWith("Chessable:", msg);
    }

    [Theory]
    [InlineData("{\"homeData\":{\"booksList\":[]}}")] // gültige (leere) Kursliste → kein Fehler
    [InlineData("{}")]
    [InlineData("not json")]
    public void TryGetChessableErrorMessage_NoError_ReturnsNull(string body)
    {
        Assert.Null(ChessableHttpService.TryGetChessableErrorMessage(body));
    }

    // --- HTML-statt-JSON-Antwort (Fix: „'<' is an invalid start of a value" leakte in die UI) ---

    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>Login</title></head></html>")]
    [InlineData("   \n <html>blocked</html>")]                 // führende Whitespaces ignoriert
    [InlineData("<?xml version=\"1.0\"?><error/>")]
    public void LooksLikeHtml_HtmlBody_ReturnsTrue(string body)
    {
        Assert.True(ChessableHttpService.LooksLikeHtml(body));
    }

    [Theory]
    [InlineData("{\"homeData\":{\"booksList\":[]}}")]          // echtes JSON-Objekt
    [InlineData("  [1,2,3]")]                                   // JSON-Array (mit Whitespace)
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikeHtml_NonHtmlBody_ReturnsFalse(string body)
    {
        Assert.False(ChessableHttpService.LooksLikeHtml(body));
    }

    // --- Unterscheidung „Token abgelaufen" vs. „IP/Zugriff blockiert" (Cloudflare 403) ---

    private const string CloudflareBlock =
        "<!DOCTYPE html><html><head><title>Chessable</title></head><body>" +
        "Sorry, you have been blocked. Cloudflare Ray ID: 8abc123</body></html>";

    /// <summary>Baut ein minimales JWT (header.payload.sig) mit gegebenem exp-Unix-Timestamp.</summary>
    private static string Jwt(long expUnix)
    {
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
        var payload = B64($"{{\"exp\":{expUnix},\"user\":{{\"uid\":1}}}}");
        return $"{header}.{payload}.sig";
    }

    [Fact]
    public void ClassifyBlockedResponse_ExpiredBearer_SaysTokenExpired()
    {
        var expired = Jwt(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
        var msg = ChessableHttpService.ClassifyBlockedResponse(CloudflareBlock, expired);
        Assert.Contains("abgelaufen", msg);
        Assert.Contains("neu hinterlegen", msg);
        Assert.DoesNotContain("VPN", msg); // klar Token, nicht IP
    }

    [Fact]
    public void ClassifyBlockedResponse_ValidBearer_CloudflareBlock_PointsToVpnIp()
    {
        var valid = Jwt(DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
        var msg = ChessableHttpService.ClassifyBlockedResponse(CloudflareBlock, valid);
        Assert.Contains("blockiert", msg);
        Assert.Contains("VPN", msg);          // verweist auf die IP, nicht den Token
        Assert.Contains("403", msg);
    }

    [Fact]
    public void ClassifyBlockedResponse_ValidBearer_GenericHtml_StaysAmbiguousButClean()
    {
        var valid = Jwt(DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
        var msg = ChessableHttpService.ClassifyBlockedResponse("<html><body>nope</body></html>", valid);
        Assert.Contains("kein gültiges JSON", msg);
        Assert.DoesNotContain("'<'", msg);    // nie der rohe Parser-Text
    }

    [Theory]
    [InlineData("Sorry, you have been blocked Cloudflare Ray ID: abc", true)]
    [InlineData("<title>Attention Required! | Cloudflare</title>", true)]
    [InlineData("<html><body>normale Seite</body></html>", false)]
    [InlineData("{\"homeData\":{}}", false)]
    public void IsCloudflareBlockPage_DetectsBlockMarkers(string body, bool expected)
    {
        Assert.Equal(expected, ChessableHttpService.IsCloudflareBlockPage(body));
    }

    // --- curl-Arg-Injektion (Fix HIGH: bid/url floss vorher als "{url}" in einen Args-String) ---

    [Fact]
    public void BuildGetArgs_MaliciousUrl_StaysSingleArgument_NoInjectedFlags()
    {
        // Eine bid mit  " -o /tmp/pwn --config /etc/passwd  hätte vorher curl-Flags eingeschleust
        // (Datei schreiben/lesen). Als ArgumentList-Token ist die KOMPLETTE URL genau ein Argument.
        var evil = "https://www.chessable.com/api/v1/getCourse?uid=1&bid=1\" -o /tmp/pwn --config /etc/passwd";
        var args = ChessableHttpService.BuildGetArgs(evil, "tok");

        Assert.Equal(evil, args[^1]);                       // ganze bösartige URL = genau ein, letztes Token
        Assert.Single(args, a => a == evil);
        Assert.DoesNotContain("-o", args);                  // kein eingeschleustes Flag als eigenes argv-Token
        Assert.DoesNotContain("--config", args);
        Assert.DoesNotContain("/tmp/pwn", args);
    }

    [Fact]
    public void BuildGetArgs_BearerAndUrl_AreDistinctSingleTokens()
    {
        var args = ChessableHttpService.BuildGetArgs("https://x/y", "my.jwt.token");
        Assert.Equal("-s", args[0]);
        Assert.Contains("-H", args);
        Assert.Contains("authorization: Bearer my.jwt.token", args); // Header-Wert = ein Token (mit Leerzeichen)
        Assert.Equal("https://x/y", args[^1]);                       // URL zuletzt, ein Token
    }

    [Fact]
    public void BuildPostArgs_PostWithStdinBody_AndUrlLast()
    {
        var args = ChessableHttpService.BuildPostArgs("https://www.chessable.com/api/v1/authenticate");
        Assert.Contains("-X", args);
        Assert.Contains("POST", args);
        Assert.Contains("-d", args);
        Assert.Contains("@-", args);                                  // Body aus stdin
        Assert.Equal("https://www.chessable.com/api/v1/authenticate", args[^1]);
    }
    [Fact]
    public void BuildGetArgs_SetsConnectTimeout30()
    {
        var args = ChessableHttpService.BuildGetArgs("https://www.chessable.com/api/v1/getGame?oid=1", "bearer");
        var i = args.IndexOf("--connect-timeout");
        Assert.True(i >= 0, "--connect-timeout fehlt");
        Assert.Equal("30", args[i + 1]);
    }

    [Fact]
    public void BuildPostArgs_SetsConnectTimeout30()
    {
        var args = ChessableHttpService.BuildPostArgs("https://www.chessable.com/api/v1/authenticate");
        var i = args.IndexOf("--connect-timeout");
        Assert.True(i >= 0, "--connect-timeout fehlt");
        Assert.Equal("30", args[i + 1]);
    }
}
