using System.Text.RegularExpressions;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;

namespace PirateChess.Api.Services;

/// <summary>
/// Audit der Chessable-Rohantworten: je curl-Lauf eine Zeile in <c>ChessableRawResponses</c> (Body gzip+Base64,
/// Login-JWT redigiert). Reines Audit/Debug — ein Fehler beim Speichern bricht den Abruf nie ab.
/// </summary>
public sealed class RawResponseAudit(IServiceScopeFactory scopeFactory, ILogger<RawResponseAudit> logger)
{
    public async Task PersistAsync(string endpoint, string? chessableUid, string url,
        int statusCode, string body, int durationMs, string? errorMessage, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChessableRawResponses.Add(new ChessableRawResponse
            {
                Endpoint = endpoint,
                ChessableUid = chessableUid,
                Url = url.Length > 500 ? url[..500] : url,
                StatusCode = statusCode,
                // gzip+Base64: die Roh-Bodies (Linien Ø ~210 KB, Kapitel Ø ~500 KB) waren bisher
                // unkomprimiert der mit Abstand größte Tabellen-Anteil. Niemand liest RawJson im Code
                // (reines Audit/Debug) → Kompression ist verhaltensneutral, ~3× kleiner.
                // Login-Antworten enthalten ein frisches Chessable-JWT → vor dem Speichern redigieren.
                RawJson = GzipText.Compress(RedactForStorage(endpoint, body ?? string.Empty)),
                DurationMs = durationMs,
                ErrorMessage = errorMessage,
                RequestedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Logging-Persistenz darf den eigentlichen Call nicht killen.
            logger.LogWarning(ex, "Failed to persist ChessableRawResponse for {Endpoint}", endpoint);
        }
    }

    /// <summary>Redigiert sensible Werte aus einem Roh-Body vor dem Audit-Speichern. Aktuell: das
    /// <c>jwt</c>-Feld der Login-Antwort (frisches Chessable-Token) → <c>[redacted]</c>. Andere
    /// Endpunkte bleiben unverändert (reine Kurs-/Linien-Daten, kein Geheimnis).</summary>
    internal static string RedactForStorage(string endpoint, string body)
    {
        if (endpoint != "login" || string.IsNullOrEmpty(body)) return body;
        // "jwt":"<token>" → "jwt":"[redacted]" (tolerant ggü. Whitespace; Token enthält keine ").
        return Regex.Replace(body, "(\"jwt\"\\s*:\\s*\")[^\"]*(\")", "$1[redacted]$2");
    }
}
