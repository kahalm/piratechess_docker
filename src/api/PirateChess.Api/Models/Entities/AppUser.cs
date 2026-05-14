namespace PirateChess.Api.Models.Entities;

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    public List<ChessableCredential> ChessableCredentials { get; set; } = [];
    public List<CachedCourse> CachedCourses { get; set; } = [];
    public List<GeneratedPgn> GeneratedPgns { get; set; } = [];
    public List<ExportHistory> ExportHistories { get; set; } = [];
}
