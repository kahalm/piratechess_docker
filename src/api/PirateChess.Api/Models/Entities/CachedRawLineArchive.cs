namespace PirateChess.Api.Models.Entities;

/// <summary>
/// Alter Inhalt einer als ungültig markierten Linie, den eine gültige ersetzt hat. Der Cache hält je oid nur
/// EINE Zeile; ohne dieses Archiv ginge der alte Stand beim Heilen verloren — und ob er wirklich kaputt war
/// oder nur die Prüfung bzw. der Parser irrte, zeigt sich manchmal erst später.
/// </summary>
public class CachedRawLineArchive
{
    public int Id { get; set; }
    public int Oid { get; set; }
    public string LineJsonContent { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; }
    public DateTime? InvalidAt { get; set; }
    public string? InvalidReason { get; set; }
    public DateTime ArchivedAt { get; set; } = DateTime.UtcNow;
}
