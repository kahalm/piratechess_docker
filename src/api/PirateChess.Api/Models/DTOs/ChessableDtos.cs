namespace PirateChess.Api.Models.DTOs;

public record SaveCredentialRequest(bool UseBearer, string? Bearer, string? Email, string? Password);
public record CredentialResponse(int Id, bool UseBearer, bool HasCredentials, string? MaskedBearer, string? MaskedEmail, string? MaskedPassword);
public record CourseListItem(string Bid, string Name);
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
