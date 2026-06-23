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
}
