using System.Text.Json;
using piratechess_lib;

namespace PirateChess.Api.Services;

/// <summary>
/// Reine Klassifikation einer Chessable-Antwort (ohne Abruf, ohne Zustand): HTML statt JSON, Cloudflare-Block,
/// IP-Soft-Block, Chessable-Fehlerkörper, transienter Proxy-Ausfall — und die handlungsleitende Meldung dazu.
/// Aus <see cref="ChessableHttpService"/> herausgelöst (Zerlegung Z46).
/// </summary>
public static class ChessableResponseClassifier
{
    /// <summary>True, wenn der Body offensichtlich HTML/XML statt JSON ist (erstes
    /// Nicht-Whitespace-Zeichen ist <c>&lt;</c>) — typisch für Login-Redirects,
    /// Cloudflare-Block- oder Proxy-Gateway-Seiten.</summary>
    internal static bool LooksLikeHtml(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        foreach (var c in content)
        {
            if (char.IsWhiteSpace(c)) continue;
            return c == '<';
        }
        return false;
    }

    /// <summary>True, wenn der Body eine Cloudflare-Block-/Challenge-Seite ist
    /// (HTTP 403 „you have been blocked" / „Attention Required" / Ray-ID).</summary>
    internal static bool IsCloudflareBlockPage(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        return (content.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
                && (content.Contains("Ray ID", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("cf-ray", StringComparison.OrdinalIgnoreCase)))
            || content.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)
            || content.Contains("you have been blocked", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Übersetzt eine Nicht-JSON-/Block-Antwort in eine handlungsleitende Meldung und unterscheidet
    /// dabei die beiden grundverschiedenen Ursachen:
    /// <list type="bullet">
    /// <item><b>Token abgelaufen/ungültig</b> → der User muss einen neuen Bearer hinterlegen.</item>
    /// <item><b>Zugriff blockiert</b> (Cloudflare-403, Token aber NICHT lokal abgelaufen) → mit
    /// hoher Wahrscheinlichkeit die VPN-Ausgangs-IP gesperrt (z. B. M247) → IP rotieren/Server wechseln.</item>
    /// </list>
    /// Diskriminator ist der lokal aus dem JWT lesbare <c>exp</c>-Claim (<see cref="JwtHelper.IsExpired"/>):
    /// ein nicht abgelaufener Bearer + Cloudflare-Block deutet auf die IP, nicht auf den Token.
    /// </summary>
    internal static string ClassifyBlockedResponse(string content, string? bearer)
    {
        bool tokenExpired = !string.IsNullOrEmpty(bearer) && SafeIsExpired(bearer);
        if (tokenExpired)
            return "Chessable-Token ist abgelaufen — bitte den Bearer neu hinterlegen.";

        if (IsCloudflareBlockPage(content))
            return "Zugriff von Chessable/Cloudflare blockiert (HTTP 403). Der Token ist nicht abgelaufen → " +
                   "sehr wahrscheinlich ist die VPN-Ausgangs-IP gesperrt: IP rotieren bzw. VPN-Server wechseln. " +
                   "Falls das nicht hilft, den Bearer prüfen.";

        // HTML/sonstige Nicht-JSON-Antwort ohne eindeutige Cloudflare-Marker.
        return "Chessable lieferte kein gültiges JSON (Token ungültig oder Zugriff blockiert) — " +
               "bitte den Bearer neu hinterlegen bzw. die VPN-IP prüfen.";
    }

    private static bool SafeIsExpired(string bearer)
    {
        try { return JwtHelper.IsExpired(bearer); }
        catch { return false; }
    }

    /// <summary>Erkennt einen Chessable-Fehler-Body (z. B. <c>{"error":{"message":"Expired token"}}</c>
    /// oder <c>{"error":"…"}</c>) und liefert eine sprechende Meldung; sonst <c>null</c>.</summary>
    public static string? TryGetChessableErrorMessage(string content) => TryGetChessableErrorMessage(content, out _);

    /// <summary>Wie <see cref="TryGetChessableErrorMessage(string)"/>; <paramref name="tokenError"/> sagt, ob die
    /// Meldung das Token betrifft („Expired token"/„Invalid token") — dann hilft kein Retry.</summary>
    internal static string? TryGetChessableErrorMessage(string content, out bool tokenError)
    {
        tokenError = false;
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var err))
                return null;
            var message = err.ValueKind switch
            {
                JsonValueKind.String => err.GetString(),
                // `error.message` bevorzugen; ist sie leer (z. B. BOOK_NOT_OWNED liefert message:"" und
                // die eigentliche Meldung in userFriendlyErrorMessage), auf userFriendlyErrorMessage
                // zurückfallen. Sonst würde ein „You do not own this course" fälschlich als „Course has
                // no chapters" durchrutschen.
                JsonValueKind.Object => StringProp(err, "message") is { Length: > 0 } m ? m : StringProp(err, "userFriendlyErrorMessage"),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(message)) return null;
            // „Expired token" / „Invalid token" → eindeutiger Hinweis auf einen neuen Bearer.
            tokenError = message.Contains("token", StringComparison.OrdinalIgnoreCase);
            return tokenError
                ? $"Chessable-Token abgelaufen/ungültig ({message}) — bitte den Bearer neu hinterlegen."
                : $"Chessable: {message}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Liefert eine String-Property eines JSON-Objekts oder null (nicht vorhanden/kein String).</summary>
    private static string? StringProp(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>True, wenn die Antwort ein IP-Soft-Block ist: leer oder nur <c>{}</c> (Länge ≤ 2)
    /// trotz erfolgreichem Transport — Chessables Signatur für eine request-rate-geblockte IP.</summary>
    internal static bool IsSoftBlockedBody(string? body) => string.IsNullOrEmpty(body) || body.Length <= 2;

    /// <summary>
    /// Erkennt einen transienten gluetun-Proxy-Ausfall: curl bricht mit „CONNECT
    /// tunnel failed, response 503" ab, während die VPN-IP rotiert/reconnectet.
    /// Solche Fehler sind nach wenigen Sekunden von selbst weg → ein Retry lohnt,
    /// im Gegensatz zu echten Fehlern (DNS, 401, …).
    /// </summary>
    public static bool IsTransientProxyFailure(int exitCode, string? error)
    {
        if (string.IsNullOrEmpty(error))
            return false;

        return error.Contains("CONNECT tunnel failed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("response 503", StringComparison.OrdinalIgnoreCase)
            || error.Contains("HTTP code 503 from proxy", StringComparison.OrdinalIgnoreCase);
    }
}
