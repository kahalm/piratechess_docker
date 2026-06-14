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
}
