namespace PirateChess.Api.Models.Entities;

public class CachedCourse
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string ChessableBid { get; set; } = string.Empty;
    public string CourseName { get; set; } = string.Empty;
    public string RestResponseJson { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;

    public AppUser User { get; set; } = null!;
    public List<GeneratedPgn> GeneratedPgns { get; set; } = [];
}
