namespace PirateChess.Api.Models.Entities;

/// <summary>
/// Eine Zeile pro Chessable-HTTP-Call: persistiert den unveraenderten
/// JSON-Body (oder leeren String bei Fehlern), Endpoint-Tag, Status-Code,
/// Dauer und ggf. Fehlermeldung. Dient als Audit- und Debug-Trail fuer die
/// Listing- und Login-Pfade — der Export-Pfad bundlet die Roh-JSONs zusaetzlich
/// in <see cref="CachedCourse.RestResponseJson"/>, hier liegt jeder Einzel-Call
/// nochmal granular nebeneinander.
/// </summary>
public class ChessableRawResponse
{
    public int Id { get; set; }

    /// <summary>Aus dem Bearer extrahierte Chessable-UID. Bei Login (Bearer noch nicht da) null.</summary>
    public string? ChessableUid { get; set; }

    /// <summary>Endpoint-Tag, z. B. <c>login</c>, <c>test</c>, <c>courses</c>, <c>course</c>, <c>chapter</c>, <c>line</c>.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Ziel-URL des Chessable-Calls (ohne Bearer/Headers).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>curl-Exit-Code (0 = ok); Antwort kann auch bei 0 leer/garbage sein.</summary>
    public int StatusCode { get; set; }

    /// <summary>Roh-Body, wie Chessable ihn geliefert hat. Bei Fehlern ggf. leer.</summary>
    public string RawJson { get; set; } = string.Empty;

    /// <summary>Dauer des HTTP-Calls in Millisekunden.</summary>
    public int DurationMs { get; set; }

    /// <summary>Fehlermeldung, falls der Call selbst geworfen hat (Timeout, Process-Fail, …). Sonst null.</summary>
    public string? ErrorMessage { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
}
