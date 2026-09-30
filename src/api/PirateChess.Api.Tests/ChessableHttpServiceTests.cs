using System;
using System.Text;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class ChessableHttpServiceTests
{
    // --- IP-Soft-Block-Erkennung (leeres {} → IP retiren + rotieren statt 30s warten) ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("  ")]   // 2 Zeichen
    public void IsSoftBlockedBody_EmptyOrBrace_ReturnsTrue(string? body)
    {
        Assert.True(ChessableHttpService.IsSoftBlockedBody(body));
    }

    [Theory]
    [InlineData("{\"game\":{}}")]
    [InlineData("[1,2,3]")]
    [InlineData("<html>")]   // HTML ist KEIN Soft-Block (eigene Klassifikation)
    public void IsSoftBlockedBody_RealPayload_ReturnsFalse(string body)
    {
        Assert.False(ChessableHttpService.IsSoftBlockedBody(body));
    }

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

    [Fact]
    public void TryGetChessableErrorMessage_BookNotOwned_UsesUserFriendlyMessage()
    {
        // Beobachteter getCourse-Body für nicht besessene Kurse: message ist LEER, die echte
        // Meldung steht in userFriendlyErrorMessage. Früher rutschte das als „Course has no chapters" durch.
        const string body = "{\"error\":{\"userFriendlyErrorMessage\":\"You do not own this course.\",\"message\":\"\",\"code\":\"BOOK_NOT_OWNED\"},\"hash\":\"124\"}";
        var msg = ChessableHttpService.TryGetChessableErrorMessage(body);
        Assert.NotNull(msg);
        Assert.Contains("You do not own this course", msg);
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
        var args = ChessableHttpService.BuildGetArgs(evil);

        Assert.Equal(evil, args[^1]);                       // ganze bösartige URL = genau ein, letztes Token
        Assert.Single(args, a => a == evil);
        Assert.DoesNotContain("-o", args);                  // kein eingeschleustes Flag als eigenes argv-Token
        Assert.DoesNotContain("--config", args);
        Assert.DoesNotContain("/tmp/pwn", args);
    }

    // --- Bearer nicht in argv (S2-006: /proc/<pid>/cmdline ist für jedes lokale Konto lesbar) ---

    [Fact]
    public void BuildGetArgs_ContainsNoBearer_AuthorizationHeaderComesFromStdin()
    {
        var args = ChessableHttpService.BuildGetArgs("https://x/y");
        Assert.Equal("-s", args[0]);
        Assert.DoesNotContain(args, a => a.Contains("Bearer", StringComparison.OrdinalIgnoreCase)
                                         || a.Contains("authorization", StringComparison.OrdinalIgnoreCase));
        var i = args.IndexOf("@-");
        Assert.True(i > 0, "-H @- fehlt");
        Assert.Equal("-H", args[i - 1]);                              // Header-Datei = stdin
        Assert.Equal("https://x/y", args[^1]);                       // URL zuletzt, ein Token
        Assert.Equal("authorization: Bearer my.jwt.token\n", ChessableHttpService.BuildGetHeaderStdin("my.jwt.token"));
    }

    // Golden-Test: Reihenfolge der curl-Argumente (TLS-/Header-Fingerprint). „-H @-" steht exakt dort, wo
    // vorher „-H authorization: Bearer …" stand; curl liest die Header-Datei an dieser Stelle ein, der
    // Request auf der Leitung ist byte-gleich (mit curl-impersonate 0.6.1 / curl 8.1.1 geprüft).
    [Fact]
    public void BuildGetArgs_GoldenOrder()
        => Assert.Equal(GoldenGetArgv("https://www.chessable.com/api/v1/getGame?lng=en&uid=1&oid=2", "17"),
            ChessableHttpService.BuildGetArgs("https://www.chessable.com/api/v1/getGame?lng=en&uid=1&oid=2", 17));

    /// <summary>Erwartete GET-Argumentliste als Literal (unabhängig vom Produktionscode); auch der Golden-Test des
    /// kompletten Kursabrufs (ChessableHttpServiceFetchTests) vergleicht gegen sie.</summary>
    internal static List<string> GoldenGetArgv(string url, string maxTimeSec)
        => new() { "-s", "-S", "--connect-timeout", "30", "--max-time", maxTimeSec,
            "--ciphers", "TLS_AES_128_GCM_SHA256,TLS_AES_256_GCM_SHA384,TLS_CHACHA20_POLY1305_SHA256,"
                + "ECDHE-ECDSA-AES128-GCM-SHA256,ECDHE-RSA-AES128-GCM-SHA256,ECDHE-ECDSA-AES256-GCM-SHA384,"
                + "ECDHE-RSA-AES256-GCM-SHA384,ECDHE-ECDSA-CHACHA20-POLY1305,ECDHE-RSA-CHACHA20-POLY1305,"
                + "ECDHE-RSA-AES128-SHA,ECDHE-RSA-AES256-SHA,AES128-GCM-SHA256,AES256-GCM-SHA384,AES128-SHA,AES256-SHA",
            "--http2", "--http2-no-server-push", "--compressed", "--tlsv1.2", "--alps", "--tls-permute-extensions",
            "--cert-compression", "brotli",
            "-H", "user-agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:138.0) Gecko/20100101 Firefox/138.0",
            "-H", "accept: application/json, text/plain, */*",
            "-H", "accept-language: en",
            "-H", "platform: Web",
            "-H", "x-os-name: Firefox",
            "-H", "x-os-version: 138",
            "-H", "x-device-model: Windows",
            "-H", "@-",
            "-H", "alt-used: www.chessable.com",
            "-H", "connection: keep-alive",
            "-H", "sec-fetch-dest: empty",
            "-H", "sec-fetch-mode: cors",
            "-H", "sec-fetch-site: same-origin",
            "-H", "priority: u=0",
            "-H", "te: trailers",
            "-H", "pragma: no-cache",
            "-H", "cache-control: no-cache",
            url };

    // Ein Zeilenumbruch im (vom Aufrufer gelieferten) Bearer darf in der Header-Datei keinen zweiten Header
    // erzeugen: curl liest jede Zeile als eigenen Header.
    [Fact]
    public void BuildGetHeaderStdin_StripsLineBreaks_SingleHeaderLine()
    {
        var text = ChessableHttpService.BuildGetHeaderStdin("a.b.c\r\nx-injected: 1\n");
        Assert.Equal("authorization: Bearer a.b.cx-injected: 1\n", text);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
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
        var args = ChessableHttpService.BuildGetArgs("https://www.chessable.com/api/v1/getGame?oid=1");
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
