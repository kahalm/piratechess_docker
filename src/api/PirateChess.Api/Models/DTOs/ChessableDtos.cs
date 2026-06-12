namespace PirateChess.Api.Models.DTOs;

public record SaveCredentialRequest(bool UseBearer, string? Bearer, string? Email, string? Password);
public record CredentialResponse(int Id, bool UseBearer, bool HasCredentials, string? MaskedBearer, string? MaskedEmail, string? MaskedPassword);
public record CourseListItem(string Bid, string Name);

// Service-to-service (rookhub → piratechess) DTOs for the stateless
// /api/chessable/direct/* endpoints. The caller passes the Chessable bearer
// per request; piratechess never persists it.
public record DirectBearerRequest(string Bearer);
public record DirectTestResponse(string Uid, int CourseCount);

// Tiefer Kurs-Abruf für den rookhub-Import. Mode steuert die Trainingsannotation im PGN:
//   "None"         → reines Repertoire-PGN (kein Trainingszug)         → rookhub-Repertoire
//   "FirstKeyMove" → erster Key-Zug je Linie trainierbar ([%tqu ...])  → rookhub-Buch (default)
//   "AllKeyMoves"  → alle Key-Züge trainierbar
public record DirectCourseRequest(string Bearer, string Bid, string? Mode);
public record DirectCourseResponse(string Bid, string Name, string Mode, int ChapterCount, int LineCount, string Pgn);
public record StartExportRequest(string Bid, string CourseName, string TrainingMode);
public record ExportStatusResponse(
    int Id,
    string Status,
    string ChessableBid,
    string CourseName,
    string TrainingMode,
    int ChapterCount,
    int LineCount,
    DateTime StartedAt,
    DateTime? CompletedAt);
public record ExportProgressMessage(
    int ExportId,
    string Phase,
    string Detail,
    int ChaptersDone,
    int ChaptersTotal,
    int LinesDone);
