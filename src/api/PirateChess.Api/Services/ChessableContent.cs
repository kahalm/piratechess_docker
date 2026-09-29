using System.Text.Json;
using piratechess_lib;

namespace PirateChess.Api.Services;

/// <summary>
/// Die EINE Prüfregel für rohe Chessable-Inhalte (getGame-Linie, getList-Kapitel). Server-Abruf, Linien-Cache,
/// Kurs-Cache und Rekonstruktion entscheiden damit gleich, ob ein Inhalt taugt — vorher gab es dafür vier
/// verschiedene Regeln, und ein Inhalt konnte an einer Stelle als ungültig markiert und an der nächsten als
/// verwertbar in einen Import gefüllt werden.
///
/// Geprüft wird, wie der Parser liest (piratechess_lib deserialisiert <see cref="ResponseLine"/> bzw.
/// <see cref="ResponseChapter"/>), dazu die Fälle, die der Parser still als leere Linie bzw. leeres Kapitel
/// durchließe: eine Linie ohne game-Objekt (ein beliebiges JSON wie <c>{"x":1}</c>) und ein Chessable-Fehlerkörper
/// (<c>{"error":{"message":"Expired token"}}</c> kommt mit HTTP 200).
/// </summary>
public static class ChessableContent
{
    private const int ReasonMaxLength = 200;
    private static readonly JsonSerializerOptions ParserJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Leer oder nur <c>{}</c>: Chessable lieferte nichts (IP-Soft-Block bzw. tote Linie).</summary>
    public static bool IsEmpty(string? content) => string.IsNullOrWhiteSpace(content) || content == "{}";

    /// <summary>
    /// Warum ein Linien-Inhalt (getGame) nicht taugt, oder <c>null</c>. Dieselbe Deserialisierung wie der Parser
    /// (sonst „Linien-JSON übersprungen (korrupt/abgeschnitten)"), dazu das game-Objekt. Fehlt es und ist der
    /// Inhalt ein Chessable-Fehlerkörper, nennt der Grund dessen Meldung.
    /// </summary>
    public static string? LineReason(string? content)
    {
        if (IsEmpty(content)) return "leer";
        try
        {
            JsonSerializer.Deserialize<ResponseLine>(content!, ParserJsonOptions);
        }
        catch (JsonException ex)
        {
            return Trim("JSON: " + ex.Message);
        }
        if (BrowserCourseAssembler.HasGameObject(content!)) return null;
        return ErrorReason(content!) ?? "kein game-Objekt";
    }

    /// <summary>
    /// Warum ein Kapitel-Inhalt (getList) nicht taugt, oder <c>null</c>. Dieselbe Deserialisierung wie der Parser,
    /// dazu der Chessable-Fehlerkörper, der sonst als Kapitel ohne Linien durchginge. Ein legitim leeres Kapitel
    /// (<c>{"list":{"data":[]}}</c>) ist gültig.
    /// </summary>
    public static string? ChapterReason(string? content)
    {
        if (IsEmpty(content)) return "leer";
        try
        {
            if (JsonSerializer.Deserialize<ResponseChapter>(content!, ParserJsonOptions) is null)
                return "kein Kapitel-Objekt";
        }
        catch (JsonException ex)
        {
            return Trim("JSON: " + ex.Message);
        }
        return ErrorReason(content!);
    }

    private static string? ErrorReason(string content)
        => ChessableHttpService.TryGetChessableErrorMessage(content) is { } message ? Trim(message) : null;

    private static string Trim(string s) => s.Length <= ReasonMaxLength ? s : s[..ReasonMaxLength];
}
