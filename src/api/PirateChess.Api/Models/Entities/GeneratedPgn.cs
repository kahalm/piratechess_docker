namespace PirateChess.Api.Models.Entities;

public class GeneratedPgn
{
    public int Id { get; set; }
    public int CachedCourseId { get; set; }
    public int UserId { get; set; }
    public string TrainingMode { get; set; } = string.Empty;
    public string PgnContent { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    public CachedCourse CachedCourse { get; set; } = null!;
    public AppUser User { get; set; } = null!;
}
