namespace PirateChess.Api.Models.Entities;

/// <summary>
/// Persistierte rohe getGame-Antwort EINER Kurs-Linie, je Chessable-Linien-ID (oid). Die oid ist
/// global eindeutig und der Inhalt user-/kursunabhängig → eine einmal erfolgreich geholte Linie
/// muss bei einem (Neu-)Start nicht erneut bei Chessable abgefragt werden. Bricht ein Kursabruf
/// mittendrin ab, holt der Neustart nur die noch fehlenden Linien (Resume).
/// </summary>
public class CachedRawLine
{
    public int Id { get; set; }
    public int Oid { get; set; }
    public string LineJsonContent { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Seit wann die Linie als ungültig gilt (JSON kaputt/abgeschnitten, kein game-Objekt). Markierte Linien
    /// werden nie gelöscht und nie verwendet: sie gelten nicht als gecacht und füllen keinen Import. So lässt
    /// sich eine zu strenge Prüfung oder ein Parser-Fehler später beheben, ohne dass Daten verloren sind.
    /// </summary>
    public DateTime? InvalidAt { get; set; }

    /// <summary>Warum die Linie markiert wurde (z. B. die JSON-Fehlermeldung), höchstens 200 Zeichen.</summary>
    public string? InvalidReason { get; set; }
}
