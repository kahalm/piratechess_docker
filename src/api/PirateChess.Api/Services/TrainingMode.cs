namespace PirateChess.Api.Services;

/// <summary>
/// Trainingsannotation des erzeugten PGN. Eine Stelle für Prüfung und Abbildung auf die piratechess_lib-Flags
/// (S2-015) — vorher je Route und im Export-Dienst kopiert.
/// </summary>
public enum TrainingMode
{
    /// <summary>Jeder Key-Zug ist trainierbar.</summary>
    AllKeyMoves,
    /// <summary>Nur der erste Key-Zug ist trainierbar (rookhub: Buch).</summary>
    FirstKeyMove,
    /// <summary>Kein Trainingszug (rookhub: Repertoire).</summary>
    None,
}

public static class TrainingModes
{
    /// <summary>Fehlermeldung der direct-Routen bei unbekanntem Modus (HTTP-Vertrag, unverändert).</summary>
    public const string InvalidModeMessage = "Invalid mode. Use: AllKeyMoves, FirstKeyMove, None";

    /// <summary>Nur der exakte Name (Groß-/Kleinschreibung zählt, keine Zahlen wie bei Enum.TryParse) — wie die
    /// frühere Prüfung gegen die Namensliste.</summary>
    public static bool TryParse(string? value, out TrainingMode mode)
    {
        switch (value)
        {
            case "AllKeyMoves": mode = TrainingMode.AllKeyMoves; return true;
            case "FirstKeyMove": mode = TrainingMode.FirstKeyMove; return true;
            case "None": mode = TrainingMode.None; return true;
            default: mode = default; return false;
        }
    }

    /// <summary>Wie <see cref="TryParse"/>, ein leerer Wert gilt als <paramref name="fallback"/> (Standard je Route).</summary>
    public static bool TryParseOrDefault(string? value, TrainingMode fallback, out TrainingMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = fallback;
            return true;
        }
        return TryParse(value, out mode);
    }

    /// <summary>Setzt die Trainings-Flags der Bibliothek für diesen Modus.</summary>
    public static void ApplyTo(this TrainingMode mode, piratechess_lib.PirateChessLib lib)
    {
        lib.AllKeyMovesTraining = mode == TrainingMode.AllKeyMoves;
        lib.NoTrainingMove = mode == TrainingMode.None;
    }
}
