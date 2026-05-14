namespace PirateChess.Api.Models.Entities;

public class ExportHistory
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string ChessableBid { get; set; } = string.Empty;
    public string CourseName { get; set; } = string.Empty;
    public string TrainingMode { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public int ChapterCount { get; set; }
    public int LineCount { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public AppUser User { get; set; } = null!;
}
