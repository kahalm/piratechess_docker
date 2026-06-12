namespace PirateChess.Api.Models.Entities;

/// <summary>
/// Persistierte rohe Kursstruktur (RestResponseCourse als JSON) je Chessable-bid — kurs-,
/// nicht userbezogen. Der Kursinhalt ist für alle Besitzer identisch; damit kann ein zweiter
/// User denselben Kurs importieren, OHNE dass Chessable erneut abgefragt wird.
/// </summary>
public class CachedRawCourse
{
    public int Id { get; set; }
    public string Bid { get; set; } = string.Empty;
    public string RestResponseJson { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;
}
