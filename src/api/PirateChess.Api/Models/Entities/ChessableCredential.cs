namespace PirateChess.Api.Models.Entities;

public class ChessableCredential
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public bool UseBearer { get; set; }
    public string? EncryptedBearer { get; set; }
    public string? EncryptedEmail { get; set; }
    public string? EncryptedPassword { get; set; }

    public AppUser User { get; set; } = null!;
}
