namespace PirateChess.Api.Models.DTOs;

public record SaveCredentialRequest(bool UseBearer, string? Bearer, string? Email, string? Password);
public record CredentialResponse(int Id, bool UseBearer, bool HasCredentials, string? MaskedBearer, string? MaskedEmail, string? MaskedPassword);
public record CourseListItem(string Bid, string Name);

// Service-to-service (rookhub → piratechess) DTOs for the stateless
// /api/chessable/direct/* endpoints. The caller passes the Chessable bearer
// per request; piratechess never persists it.
// TunnelIndex (optional, 0-basiert): wenn gesetzt, läuft der Test-Request fix über GENAU diesen
// VPN-Tunnel (gezielter „über diesen VPN testen") statt über das round-robin. Nur vom /test-Endpoint
// ausgewertet; /courses ignoriert ihn. Fehlt das Feld → null → bisheriges Verhalten.
public record DirectBearerRequest(string Bearer, int? TunnelIndex = null);
// Bei gepinntem Test zusätzlich, welcher Tunnel genutzt wurde + dessen Exit-IP (best-effort).
public record DirectTestResponse(string Uid, int CourseCount, int? TunnelIndex = null, string? TunnelProxy = null, string? ExitIp = null);

// Tiefer Kurs-Abruf für den rookhub-Import. Mode steuert die Trainingsannotation im PGN:
//   "None"         → reines Repertoire-PGN (kein Trainingszug)         → rookhub-Repertoire
//   "FirstKeyMove" → erster Key-Zug je Linie trainierbar ([%tqu ...])  → rookhub-Buch (default)
//   "AllKeyMoves"  → alle Key-Züge trainierbar
public record DirectCourseRequest(string Bearer, string Bid, string? Mode);
public record DirectCourseResponse(string Bid, string Name, string Mode, int ChapterCount, int LineCount, string Pgn);

// Async-Variante mit Fortschritt: /course/start liefert eine JobId, /course/{jobId} pollt
// den Fortschritt (Kapitel/Linien) und liefert bei Status "completed" das fertige Pgn.
public record DirectCourseStartResponse(string JobId);
// Diagnose: eine einzelne Linie (getGame) über den echten Abruf-Pfad testen.
public record DirectLineDebugRequest(string Bearer, int Oid);
public record DirectLineDebugResponse(int Oid, string Uid, bool Ok, int Bytes, long Ms, string? Error, string Snippet);
// Leichte Vorab-Schätzung (ohne tiefen Abruf): Gesamt-Linienzahl eines Kurses. Cached=true → aus dem
// Rohdaten-Cache (kein Chessable-Call); sonst aus einem einzelnen getCourse?includeVariations.
public record DirectCourseInfoResponse(string Bid, int TotalLines, bool Cached);
public record DirectCourseProgressResponse(
    string Status,
    int ChaptersDone,
    int ChaptersTotal,
    int LinesDone,
    int LinesTotal,
    int ChapterCount,
    int LineCount,
    string? CourseName,
    string? Pgn,
    string? Error);
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
