using System.Text;
using System.Text.Json;

namespace PirateChess.Api.Services;

/// <summary>
/// Liest Identitäts-Claims aus dem Chessable-Bearer (JWT). Der Payload ist nur
/// Base64URL-codiertes JSON und ohne Secret lesbar — wir prüfen NICHT die Signatur,
/// extrahieren nur den Anzeige-Usernamen fürs Logging.
/// </summary>
public static class ChessableJwt
{
    /// <summary>
    /// Extrahiert <c>user.uname</c> aus dem JWT-Payload. Best-effort: gibt bei leerem/
    /// ungültigem Token oder fehlendem Claim <c>null</c> zurück (darf das Logging nie werfen).
    /// </summary>
    public static string? TryExtractUname(string? bearer)
    {
        if (string.IsNullOrWhiteSpace(bearer)) return null;

        var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearer["Bearer ".Length..]
            : bearer;

        var parts = token.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            using var doc = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            if (doc.RootElement.TryGetProperty("user", out var user)
                && user.TryGetProperty("uname", out var uname)
                && uname.ValueKind == JsonValueKind.String)
            {
                var value = uname.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch
        {
            // Malformed Token -> kein Username (nicht werfen, reines Logging-Detail).
        }
        return null;
    }

    private static string DecodeBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}
