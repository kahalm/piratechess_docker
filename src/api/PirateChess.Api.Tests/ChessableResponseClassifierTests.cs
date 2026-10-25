using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>
/// Charakterisierung der Antwort-Klassifikation (Zerlegung Z46): je gespeichertem Antwort-Kopf, wie ihn Chessable,
/// Cloudflare oder der gluetun-Proxy liefern, alle Urteile auf einmal — HTML ja/nein, Cloudflare-Block, IP-Soft-Block,
/// Chessable-Fehlerkörper (mit Token-Bezug) und die Meldung für eine Block-Antwort ohne Bearer.
/// </summary>
public class ChessableResponseClassifierTests
{
    private const string Generic = "Chessable lieferte kein gültiges JSON (Token ungültig oder Zugriff blockiert) — " +
                                   "bitte den Bearer neu hinterlegen bzw. die VPN-IP prüfen.";
    private const string Blocked = "Zugriff von Chessable/Cloudflare blockiert (HTTP 403). Der Token ist nicht abgelaufen → " +
                                   "sehr wahrscheinlich ist die VPN-Ausgangs-IP gesperrt: IP rotieren bzw. VPN-Server wechseln. " +
                                   "Falls das nicht hilft, den Bearer prüfen.";

    public static TheoryData<string, bool, bool, bool, string?, bool, string> Heads => new()
    {
        // body, html, cloudflare, softBlock, chessableError, tokenError, classifyWithoutBearer
        { "", false, false, true, null, false, Generic },
        { "{}", false, false, true, null, false, Generic },
        { "  \n<!DOCTYPE html><html><body>Sorry, you have been blocked. Cloudflare Ray ID: 8abc</body></html>",
            true, true, false, null, false, Blocked },
        { "<html><head><title>Attention Required! | Cloudflare</title></head></html>", true, true, false, null, false, Blocked },
        { "<html><body>cloudflare cf-ray: 1</body></html>", true, true, false, null, false, Blocked },
        { "<html><body>502 Bad Gateway</body></html>", true, false, false, null, false, Generic },
        { "{\"error\":{\"message\":\"Expired token\"}}", false, false, false,
            "Chessable-Token abgelaufen/ungültig (Expired token) — bitte den Bearer neu hinterlegen.", true, Generic },
        { "{\"error\":\"Invalid token\"}", false, false, false,
            "Chessable-Token abgelaufen/ungültig (Invalid token) — bitte den Bearer neu hinterlegen.", true, Generic },
        { "{\"error\":{\"message\":\"\",\"userFriendlyErrorMessage\":\"You do not own this course\"}}", false, false, false,
            "Chessable: You do not own this course", false, Generic },
        { "{\"error\":{\"message\":\"User is banned or deleted\"}}", false, false, false,
            "Chessable: User is banned or deleted", false, Generic },
        { "{\"error\":{\"message\":\"   \"}}", false, false, false, null, false, Generic },
        { "{\"error\":42}", false, false, false, null, false, Generic },
        { "[{\"error\":\"x\"}]", false, false, false, null, false, Generic },
        { "{\"list\":{\"data\":[", false, false, false, null, false, Generic },
        { "{\"course\":{\"data\":[]}}", false, false, false, null, false, Generic },
    };

    [Theory]
    [MemberData(nameof(Heads))]
    public void Head_IsClassifiedAsBefore(string body, bool html, bool cloudflare, bool softBlock,
        string? chessableError, bool tokenError, string classifyWithoutBearer)
    {
        Assert.Equal(html, ChessableResponseClassifier.LooksLikeHtml(body));
        Assert.Equal(cloudflare, ChessableResponseClassifier.IsCloudflareBlockPage(body));
        Assert.Equal(softBlock, ChessableResponseClassifier.IsSoftBlockedBody(body));
        Assert.Equal(chessableError, ChessableResponseClassifier.TryGetChessableErrorMessage(body));
        Assert.Equal(chessableError, ChessableResponseClassifier.TryGetChessableErrorMessage(body, out var isTokenError));
        Assert.Equal(tokenError, isTokenError);
        Assert.Equal(classifyWithoutBearer, ChessableResponseClassifier.ClassifyBlockedResponse(body, null));
        Assert.Equal(classifyWithoutBearer, ChessableResponseClassifier.ClassifyBlockedResponse(body, "kein.jwt"));
    }

    [Theory]
    [InlineData(56, "curl: (56) CONNECT tunnel failed, response 503", true)]
    [InlineData(56, "curl: (56) Received HTTP code 503 from proxy after CONNECT", true)]
    [InlineData(7, "curl: (7) Failed to connect", false)]
    [InlineData(28, "curl: (28) Operation timed out", false)]
    [InlineData(0, "", false)]
    [InlineData(0, null, false)]
    public void TransientProxyFailure_Table(int exitCode, string? stderr, bool expected)
        => Assert.Equal(expected, ChessableResponseClassifier.IsTransientProxyFailure(exitCode, stderr));
}
